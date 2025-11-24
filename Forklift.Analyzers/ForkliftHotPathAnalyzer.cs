using System;
using System.Collections.Immutable;
using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Forklift.Analyzers
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class ForkliftHotPathAnalyzer : DiagnosticAnalyzer
    {
        // Diagnostic IDs
        public const string FLK001 = "FLK001"; // new AlgebraicNotation(...) not allowed
        public const string FLK002 = "FLK002"; // Substring(...) usage

        private static readonly DiagnosticDescriptor NewAlgDesc = new(
            id: FLK001,
            title: "Avoid allocating AlgebraicNotation in hot paths",
            messageFormat: "Avoid 'new AlgebraicNotation(...)' here (use cached/interned values or span-based parsing)",
            category: "Performance",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "Disallows ad-hoc AlgebraicNotation allocations outside whitelisted files."
        );

        private static readonly DiagnosticDescriptor SubstringDesc = new(
            id: FLK002,
            title: "Avoid Substring in parsing paths",
            messageFormat: "Avoid 'Substring(...)' (use 'AsSpan()' + span-based APIs)",
            category: "Performance",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "Substring allocates; use span-based parsing."
        );

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
            ImmutableArray.Create(NewAlgDesc, SubstringDesc);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            context.RegisterSyntaxNodeAction(AnalyzeObjectCreation, SyntaxKind.ObjectCreationExpression);
            context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
        }

        private static void AnalyzeObjectCreation(SyntaxNodeAnalysisContext ctx)
        {
            var creation = (ObjectCreationExpressionSyntax)ctx.Node;
            var type = ctx.SemanticModel.GetSymbolInfo(creation.Type).Symbol as ITypeSymbol;
            if (type is null) return;

            if (type.Name == "AlgebraicNotation" && type.ContainingNamespace.ToDisplayString().EndsWith("Forklift.Core"))
            {
                // Allow-list specific files (configurable via .editorconfig if you want later)
                var file = ctx.Node.SyntaxTree.FilePath;
                var fileName = Path.GetFileName(file);
                if (!IsWhitelistedForAlgebraicNotationAlloc(fileName))
                {
                    ctx.ReportDiagnostic(Diagnostic.Create(NewAlgDesc, creation.GetLocation()));
                }
            }
        }

        private static void AnalyzeInvocation(SyntaxNodeAnalysisContext ctx)
        {
            var inv = (InvocationExpressionSyntax)ctx.Node;
            var sym = ctx.SemanticModel.GetSymbolInfo(inv).Symbol as IMethodSymbol;
            if (sym is null) return;

            // FLK002: Substring on string
            if (sym.Name == "Substring" &&
                sym.ContainingType?.SpecialType == SpecialType.System_String)
            {
                ctx.ReportDiagnostic(Diagnostic.Create(SubstringDesc, inv.GetLocation()));
            }
        }

        private static bool IsWhitelistedForAlgebraicNotationAlloc(string fileName)
        {
            // Default whitelist; adjust as needed
            // e.g., only core files that define/seed the caches should be allowed
            return fileName.Equals("Squares.cs", StringComparison.OrdinalIgnoreCase)
                || fileName.Equals("AlgebraicNotation.cs", StringComparison.OrdinalIgnoreCase);
        }
    }
}
