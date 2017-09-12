﻿using Microsoft.CodeAnalysis;
using System;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.IO;
using System.Diagnostics;

namespace ShaderGen
{
    public abstract class GlslBackendBase : LanguageBackend
    {
        public GlslBackendBase(Compilation compilation) : base(compilation)
        {
        }

        protected override string CSharpToShaderTypeCore(string fullType)
        {
            return GlslKnownTypes.GetMappedName(fullType)
                .Replace(".", "_");
        }

        protected void WriteStructure(StringBuilder sb, StructureDefinition sd)
        {
            sb.AppendLine($"struct {CSharpToShaderType(sd.Name)}");
            sb.AppendLine("{");
            StringBuilder fb = new StringBuilder();
            foreach (FieldDefinition field in sd.Fields)
            {
                fb.Append(CSharpToShaderType(field.Type.Name.Trim()));
                fb.Append(' ');
                fb.Append(CorrectIdentifier(field.Name.Trim()));
                int arrayCount = field.ArrayElementCount;
                if (arrayCount > 0)
                {
                    fb.Append('['); fb.Append(arrayCount); fb.Append(']');
                }
                fb.Append(';');
                sb.Append("    ");
                sb.AppendLine(fb.ToString());
                fb.Clear();
            }
            sb.AppendLine("};");
            sb.AppendLine();
        }


        protected override string GenerateFullTextCore(ShaderFunction function)
        {
            StringBuilder sb = new StringBuilder();

            ShaderFunctionAndBlockSyntax entryPoint = Functions.SingleOrDefault(
                sfabs => sfabs.Function.Name == function.Name);
            if (entryPoint == null)
            {
                throw new ShaderGenerationException("Couldn't find given function: " + function.Name);
            }

            StructureDefinition input = GetRequiredStructureType(entryPoint.Function.Parameters[0].Type);

            WriteVersionHeader(sb);

            foreach (StructureDefinition sd in Structures)
            {
                WriteStructure(sb, sd);
            }

            foreach (ResourceDefinition rd in Resources)
            {
                switch (rd.ResourceKind)
                {
                    case ShaderResourceKind.Uniform:
                        WriteUniform(sb, rd);
                        break;
                    case ShaderResourceKind.Texture2D:
                        WriteTexture2D(sb, rd);
                        break;
                    case ShaderResourceKind.TextureCube:
                        WriteTextureCube(sb, rd);
                        break;
                    case ShaderResourceKind.Sampler:
                        WriteSampler(sb, rd);
                        break;
                    default: throw new ShaderGenerationException("Illegal resource kind: " + rd.ResourceKind);
                }
            }

            string result = new ShaderMethodVisitor(Compilation, entryPoint.Function, this)
                .VisitFunction(entryPoint.Block);
            sb.AppendLine(result);

            WriteMainFunction(sb, entryPoint.Function);

            return sb.ToString();
        }

        private void WriteMainFunction(StringBuilder sb, ShaderFunction entryFunction)
        {
            ParameterDefinition input = entryFunction.Parameters[0];
            StructureDefinition inputType = GetRequiredStructureType(input.Type);
            StructureDefinition outputType = entryFunction.Type == ShaderFunctionType.VertexEntryPoint
                ? GetRequiredStructureType(entryFunction.ReturnType)
                : null; // Hacky but meh

            int inVarIndex = 0;
            foreach (FieldDefinition field in inputType.Fields)
            {
                WriteInOutVariable(
                    sb,
                    true,
                    CSharpToShaderType(field.Type.Name),
                    CorrectIdentifier(field.Name),
                    inVarIndex);
                inVarIndex += 1;
            }

            if (entryFunction.Type == ShaderFunctionType.VertexEntryPoint)
            {
                bool skippedFirstPositionSemantic = false;
                int outVarIndex = 0;
                foreach (FieldDefinition field in outputType.Fields)
                {
                    if (field.SemanticType == SemanticType.Position && !skippedFirstPositionSemantic)
                    {
                        skippedFirstPositionSemantic = true;
                        continue;
                    }
                    else
                    {
                        WriteInOutVariable(
                            sb,
                            false,
                            CSharpToShaderType(field.Type.Name),
                            "out_" + CorrectIdentifier(field.Name),
                            outVarIndex);
                        outVarIndex += 1;
                    }
                }
            }
            else
            {
                Debug.Assert(entryFunction.Type == ShaderFunctionType.FragmentEntryPoint);
                string mappedReturnType = CSharpToShaderType(entryFunction.ReturnType.Name);
                if (mappedReturnType != "vec4")
                {
                    throw new ShaderGenerationException("Fragment shader must return a System.Numerics.Vector4 value.");
                }

                WriteInOutVariable(sb, false, "vec4", "_outputColor_", 0);
            }

            sb.AppendLine();

            string inTypeName = CSharpToShaderType(inputType.Name);
            string outTypeName = CSharpToShaderType(entryFunction.ReturnType.Name);

            sb.AppendLine($"void main()");
            sb.AppendLine("{");
            sb.AppendLine($"    {inTypeName} {CorrectIdentifier("input")};");
            foreach (FieldDefinition field in inputType.Fields)
            {
                sb.AppendLine($"    {CorrectIdentifier("input")}.{CorrectIdentifier(field.Name)} = {CorrectIdentifier(field.Name)};");
            }

            sb.AppendLine($"    {outTypeName} {CorrectIdentifier("output")} = {entryFunction.Name}({CorrectIdentifier("input")});");

            if (entryFunction.Type == ShaderFunctionType.VertexEntryPoint)
            {
                FieldDefinition positionSemanticField = null;
                foreach (FieldDefinition field in outputType.Fields)
                {
                    if (positionSemanticField == null && field.SemanticType == SemanticType.Position)
                    {
                        positionSemanticField = field;
                    }
                    else
                    {
                        sb.AppendLine($"    out_{CorrectIdentifier(field.Name)} = {CorrectIdentifier("output")}.{CorrectIdentifier(field.Name)};");
                    }
                }

                if (positionSemanticField == null)
                {
                    // TODO: Should be caught earlier.
                    throw new ShaderGenerationException("At least one vertex output must have a position semantic.");
                }

                sb.AppendLine($"    gl_Position = {CorrectIdentifier("output")}.{CorrectIdentifier(positionSemanticField.Name)};");
            }
            else
            {
                Debug.Assert(entryFunction.Type == ShaderFunctionType.FragmentEntryPoint);
                sb.AppendLine($"    _outputColor_ = {CorrectIdentifier("output")};");
            }
            sb.AppendLine("}");
        }

        private StructureDefinition GetRequiredStructureType(TypeReference type)
        {
            StructureDefinition result = Structures.SingleOrDefault(sd => sd.Name == type.Name);
            if (result == null)
            {
                if (!TryDiscoverStructure(type.Name))
                {
                    throw new ShaderGenerationException("Type referred by was not discovered: " + type.Name);
                }
            }

            return result;
        }

        private bool TryDiscoverStructure(string name)
        {
            INamedTypeSymbol type = Compilation.GetTypeByMetadataName(name);
            SyntaxNode declaringSyntax = type.OriginalDefinition.DeclaringSyntaxReferences[0].GetSyntax();
            if (declaringSyntax is StructDeclarationSyntax sds)
            {
                if (ShaderSyntaxWalker.TryGetStructDefinition(Compilation.GetSemanticModel(sds.SyntaxTree), sds, out StructureDefinition sd))
                {
                    Structures.Add(sd);
                    return true;
                }
            }

            return false;
        }

        protected override string CSharpToIdentifierNameCore(string typeName, string identifier)
        {
            return GlslKnownIdentifiers.GetMappedIdentifier(typeName, identifier);
        }

        internal override string CorrectIdentifier(string identifier)
        {
            if (s_glslKeywords.Contains(identifier))
            {
                return identifier + "_";
            }

            return identifier;
        }

        private static readonly HashSet<string> s_glslKeywords = new HashSet<string>()
        {
            "input", "output",
        };

        protected abstract void WriteVersionHeader(StringBuilder sb);
        protected abstract void WriteUniform(StringBuilder sb, ResourceDefinition rd);
        protected abstract void WriteSampler(StringBuilder sb, ResourceDefinition rd);
        protected abstract void WriteTexture2D(StringBuilder sb, ResourceDefinition rd);
        protected abstract void WriteTextureCube(StringBuilder sb, ResourceDefinition rd);
        protected abstract void WriteInOutVariable(StringBuilder sb, bool isInVar, string normalizedType, string normalizedIdentifier, int index);
    }
}
