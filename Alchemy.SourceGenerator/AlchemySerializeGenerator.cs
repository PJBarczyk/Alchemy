﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Alchemy.SourceGenerator
{
    [Generator]
    public sealed class AlchemySerializeGenerator : ISourceGenerator
    {
        public void Initialize(GeneratorInitializationContext context)
        {
            context.RegisterForSyntaxNotifications(SyntaxReceiver.Create);
        }

        public void Execute(GeneratorExecutionContext context)
        {
            if (context.SyntaxReceiver is not SyntaxReceiver receiver || receiver.TargetTypes.Count == 0) return;

            var compilation = context.Compilation;

            try
            {
                foreach (var typeSyntax in receiver.TargetTypes)
                {
                    var typeSymbol = context.Compilation.GetSemanticModel(typeSyntax.SyntaxTree)
                        .GetDeclaredSymbol(typeSyntax);

                    if (!IsPartial(typeSyntax))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.MustBePartial, typeSyntax.Identifier.GetLocation(), typeSymbol.Name));
                        continue;
                    }

                    if (IsNested(typeSyntax))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.NestedNotAllow, typeSyntax.Identifier.GetLocation(), typeSymbol.Name));
                        continue;
                    }

                    var fieldSymbols = new List<IFieldSymbol>();
                    var fields = typeSyntax.Members
                        .Where(x => x is FieldDeclarationSyntax)
                        .Select(x => (FieldDeclarationSyntax)x);
                    foreach (var field in fields)
                    {
                        var model = context.Compilation.GetSemanticModel(field.SyntaxTree);
                        foreach (var variable in field.Declaration.Variables)
                        {
                            var fieldSymbol = model.GetDeclaredSymbol(variable) as IFieldSymbol;
                            var alchemySerializeAttribute = fieldSymbol.GetAttributes()
                                .FirstOrDefault(x =>
                                    x.AttributeClass.Name is "AlchemySerializeField"
                                                          or "AlchemySerializeFieldAttribute"
                                                          or "Alchemy.Serialization.AlchemySerializeField"
                                                          or "Alchemy.Serialization.AlchemySerializeFieldAttribute");

                            var nonSerializedAttribute = fieldSymbol.GetAttributes()
                                .FirstOrDefault(x =>
                                    x.AttributeClass.Name is "NonSerialized"
                                                          or "NonSerializedAttribute"
                                                          or "System.NonSerialized"
                                                          or "System.NonSerializedAttribute");

                            if (alchemySerializeAttribute != null)
                            {
                                if (nonSerializedAttribute == null)
                                {
                                    context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.ShouldBeNonSerialized, variable.Identifier.GetLocation(), fieldSymbol.Name));
                                }

                                fieldSymbols.Add(fieldSymbol);
                            }
                        }
                    }

                    var sourceText = ProcessClass((INamedTypeSymbol)typeSymbol, fieldSymbols);
                    var fullType = typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                        .Replace("global::", "")
                        .Replace("<", "_")
                        .Replace(">", "_");

                    context.AddSource(fullType + ".AlchemySerializeGenerator.g.cs", sourceText);
                }
            }
            catch (Exception ex)
            {
                var diagnosticDescriptor = new DiagnosticDescriptor("AlchemySerializeGeneratorError", "AlchemySerializeGeneratorError", $"Generation failed with:\n {ex}", "AlchemySerializeGeneratorError", DiagnosticSeverity.Error, true);
                context.ReportDiagnostic(Diagnostic.Create(diagnosticDescriptor, Location.None, DiagnosticSeverity.Error));
            }
        }

        static string ProcessClass(INamedTypeSymbol typeSymbol, List<IFieldSymbol> fieldSymbols)
        {
            var onAfterDeserializeCodeBuilder = new StringBuilder();
            var onBeforeSerializeCodeBuilder = new StringBuilder();
            var serializationDataCodeBuilder = new StringBuilder();

            var hasShowSerializationData = typeSymbol.GetAttributes().Any(x => x.AttributeClass.Name
                is "ShowAlchemySerializationData"
                or "ShowAlchemySerializationDataAttribute"
                or "Alchemy.Serialization.ShowAlchemySerializationData"
                or "Alchemy.Serialization.ShowAlchemySerializationDataAttribute");

            var serializationDataAttibutesCode = hasShowSerializationData ? "[global::Alchemy.Inspector.ReadOnly, global::UnityEngine.TextArea(3, 999), global::UnityEngine.SerializeField]" : "[global::UnityEngine.HideInInspector, global::UnityEngine.SerializeField]";

            // target class namespace
            var ns = typeSymbol.ContainingNamespace.IsGlobalNamespace ? string.Empty : $"namespace {typeSymbol.ContainingNamespace} {{";

            foreach (var field in fieldSymbols)
            {
                var serializeCode =
@$"try
{{
    alchemySerializationData.{field.Name}.data = global::Alchemy.Serialization.Internal.SerializationHelper.ToJson(this.{field.Name} , alchemySerializationData.UnityObjectReferences);
    alchemySerializationData.{field.Name}.isCreated = true;
}}
catch (global::System.Exception ex)
{{
    global::UnityEngine.Debug.LogException(ex);
}}";

                var deserializeCode =
@$"try 
{{
    if (alchemySerializationData.{field.Name}.isCreated)
    {{
        this.{field.Name} = global::Alchemy.Serialization.Internal.SerializationHelper.FromJson<{field.Type.ToDisplayString()}>(alchemySerializationData.{field.Name}.data, alchemySerializationData.UnityObjectReferences);
    }}
}}
catch (global::System.Exception ex)
{{
    global::UnityEngine.Debug.LogException(ex);
}}";

                onBeforeSerializeCodeBuilder.AppendLine(serializeCode);
                onAfterDeserializeCodeBuilder.AppendLine(deserializeCode);

                serializationDataCodeBuilder.Append("public Item ").Append(field.Name).Append(" = new();");
            }

            return
@$"
// <auto-generated/>
{ns}

    partial class {typeSymbol.Name} : global::UnityEngine.ISerializationCallbackReceiver
    {{
        void global::UnityEngine.ISerializationCallbackReceiver.OnAfterDeserialize()
        {{
            {onAfterDeserializeCodeBuilder}
            if (this is global::Alchemy.Serialization.IAlchemySerializationCallbackReceiver receiver) receiver.OnAfterDeserialize();
        }}

        void global::UnityEngine.ISerializationCallbackReceiver.OnBeforeSerialize()
        {{
            if (this is global::Alchemy.Serialization.IAlchemySerializationCallbackReceiver receiver) receiver.OnBeforeSerialize();
            alchemySerializationData.UnityObjectReferences.Clear();
            {onBeforeSerializeCodeBuilder}
        }}

        [global::System.Serializable]
        sealed class AlchemySerializationData
        {{
            {serializationDataCodeBuilder}

            [global::UnityEngine.SerializeField] private global::System.Collections.Generic.List<UnityEngine.Object> unityObjectReferences = new();
            public global::System.Collections.Generic.IList<UnityEngine.Object> UnityObjectReferences => unityObjectReferences;

            [global::System.Serializable]
            public sealed class Item
            {{
                [global::UnityEngine.HideInInspector] public bool isCreated = false;
                [global::UnityEngine.TextArea(1, 999)] public string data;
            }}
        }}

        {serializationDataAttibutesCode} private AlchemySerializationData alchemySerializationData = new();
    }}

{(string.IsNullOrEmpty(ns) ? "" : "}")}
";
        }

        static bool IsPartial(TypeDeclarationSyntax typeDeclaration)
        {
            return typeDeclaration.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword));
        }

        static bool IsNested(TypeDeclarationSyntax typeDeclaration)
        {
            return typeDeclaration.Parent is TypeDeclarationSyntax;
        }

        sealed class SyntaxReceiver : ISyntaxReceiver
        {
            internal static ISyntaxReceiver Create()
            {
                return new SyntaxReceiver();
            }

            public List<TypeDeclarationSyntax> TargetTypes { get; } = new();

            public void OnVisitSyntaxNode(SyntaxNode syntaxNode)
            {
                if (syntaxNode is TypeDeclarationSyntax typeDeclarationSyntax)
                {
                    var hasAttribute = typeDeclarationSyntax.AttributeLists
                            .SelectMany(x => x.Attributes)
                            .Any(x => x.Name.ToString()
                                is "AlchemySerialize"
                                or "AlchemySerializeAttribute"
                                or "Alchemy.Serialization.AlchemySerialize"
                                or "Alchemy.Serialization.AlchemySerializeAttribute");
                    if (hasAttribute)
                    {
                        TargetTypes.Add(typeDeclarationSyntax);
                    }
                }
            }
        }
    }
}