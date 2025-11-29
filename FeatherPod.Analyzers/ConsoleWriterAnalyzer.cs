using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace FeatherPod.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class ConsoleWriterAnalyzer : DiagnosticAnalyzer
{
    private const string DirectConsoleId = "FP001";
    private const string DirectAnsiConsoleId = "FP002";
    private const string EmptyWriteLineId = "FP003";
    private const string EmptyMarkupLineId = "FP004";

    private static readonly DiagnosticDescriptor DirectConsoleRule = new(
        DirectConsoleId,
        "Direct Console usage",
        "Use ConsoleWriter instead of Console.{0}()",
        "Usage",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "All console output should go through ConsoleWriter for consistent formatting.");

    private static readonly DiagnosticDescriptor DirectAnsiConsoleRule = new(
        DirectAnsiConsoleId,
        "Direct AnsiConsole usage",
        "Use ConsoleWriter instead of AnsiConsole.{0}()",
        "Usage",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "All console output should go through ConsoleWriter for consistent formatting.");

    private static readonly DiagnosticDescriptor EmptyWriteLineRule = new(
        EmptyWriteLineId,
        "Empty WriteLine",
        "Use Out.BlankLine() instead of Out.WriteLine() with null or empty string",
        "Usage",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Use BlankLine() for blank lines to maintain proper spacing logic.");

    private static readonly DiagnosticDescriptor EmptyMarkupLineRule = new(
        EmptyMarkupLineId,
        "Empty MarkupLine",
        "Use Out.BlankLine() instead of Out.MarkupLine() with null or empty string",
        "Usage",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Use BlankLine() for blank lines to maintain proper spacing logic.");

    private static readonly ImmutableHashSet<string> ConsoleForbiddenMethods = ImmutableHashSet.Create("Write", "WriteLine");
    private static readonly ImmutableHashSet<string> AnsiConsoleForbiddenMethods = ImmutableHashSet.Create("Write", "WriteLine", "Markup", "MarkupLine");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [DirectConsoleRule, DirectAnsiConsoleRule, EmptyWriteLineRule, EmptyMarkupLineRule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(compilationContext =>
        {
            // Cache type/symbol lookups once per compilation
            var consoleType = compilationContext.Compilation.GetTypeByMetadataName("System.Console");
            var ansiConsoleType = compilationContext.Compilation.GetTypeByMetadataName("Spectre.Console.AnsiConsole");
            var consoleWriterType = compilationContext.Compilation.GetTypeByMetadataName("FeatherPod.Infrastructure.ConsoleWriter");
            var stringType = compilationContext.Compilation.GetSpecialType(SpecialType.System_String);
            var stringEmptyField = stringType?.GetMembers("Empty").OfType<IFieldSymbol>().FirstOrDefault();

            compilationContext.RegisterSyntaxNodeAction(
                ctx => AnalyzeInvocation(ctx, consoleType, ansiConsoleType, consoleWriterType, stringEmptyField),
                SyntaxKind.InvocationExpression);
        });
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context, INamedTypeSymbol? consoleType, INamedTypeSymbol? ansiConsoleType, INamedTypeSymbol? consoleWriterType, IFieldSymbol? stringEmptyField)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;

        // Skip if in ConsoleWriter.cs (it's the wrapper itself)
        var filePath = context.Node.SyntaxTree.FilePath;
        if (filePath.EndsWith("ConsoleWriter.cs", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Get the method symbol for semantic analysis
        var symbolInfo = context.SemanticModel.GetSymbolInfo(invocation);
        if (symbolInfo.Symbol is not IMethodSymbol methodSymbol)
        {
            return;
        }

        var methodName = methodSymbol.Name;
        var containingType = methodSymbol.ContainingType;
        if (containingType == null)
        {
            return;
        }

        // FP001: Direct Console usage
        if (SymbolEqualityComparer.Default.Equals(containingType, consoleType) && ConsoleForbiddenMethods.Contains(methodName))
        {
            var diagnostic = Diagnostic.Create(DirectConsoleRule, invocation.GetLocation(), methodName);
            context.ReportDiagnostic(diagnostic);

            return;
        }

        // FP002: Direct AnsiConsole usage
        if (SymbolEqualityComparer.Default.Equals(containingType, ansiConsoleType) && AnsiConsoleForbiddenMethods.Contains(methodName))
        {
            var diagnostic = Diagnostic.Create(DirectAnsiConsoleRule, invocation.GetLocation(), methodName);
            context.ReportDiagnostic(diagnostic);

            return;
        }

        // FP003/FP004: Empty WriteLine/MarkupLine on ConsoleWriter
        if (SymbolEqualityComparer.Default.Equals(containingType, consoleWriterType))
        {
            if (methodName == "WriteLine" && IsNullOrEmptyArgument(invocation, context.SemanticModel, stringEmptyField))
            {
                var diagnostic = Diagnostic.Create(EmptyWriteLineRule, invocation.GetLocation());
                context.ReportDiagnostic(diagnostic);

                return;
            }

            if (methodName == "MarkupLine" && IsNullOrEmptyArgument(invocation, context.SemanticModel, stringEmptyField))
            {
                var diagnostic = Diagnostic.Create(EmptyMarkupLineRule, invocation.GetLocation());
                context.ReportDiagnostic(diagnostic);
            }
        }
    }

    private static bool IsNullOrEmptyArgument(InvocationExpressionSyntax invocation, SemanticModel semanticModel, IFieldSymbol? stringEmptyField)
    {
        var arguments = invocation.ArgumentList.Arguments;

        if (arguments.Count == 0)
        {
            return true;
        }

        if (arguments.Count != 1)
        {
            return false;
        }

        var arg = arguments[0].Expression;

        // Check for empty string literal: ""
        if (arg is LiteralExpressionSyntax literal)
        {
            if (literal.IsKind(SyntaxKind.StringLiteralExpression) && literal.Token.ValueText == "")
            {
                return true;
            }

            if (literal.IsKind(SyntaxKind.NullLiteralExpression))
            {
                return true;
            }

            if (literal.IsKind(SyntaxKind.DefaultLiteralExpression))
            {
                return true;
            }
        }

        // Check for string.Empty using semantic analysis
        if (stringEmptyField != null && arg is MemberAccessExpressionSyntax)
        {
            var symbolInfo = semanticModel.GetSymbolInfo(arg);
            if (SymbolEqualityComparer.Default.Equals(symbolInfo.Symbol, stringEmptyField))
            {
                return true;
            }
        }

        // Check for default(string)
        return arg is DefaultExpressionSyntax;
    }
}
