using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace NPlusOne.Benchmarks.Analyzer;

// A measuring stick, never shipped: the rules for analyzer projects and released diagnostics do not apply.
#pragma warning disable RS1036, RS1041, RS2008

/// <summary>
/// Subscribes to the operations the N+1 analyzer subscribes to and does nothing: the floor of any operation analyzer,
/// the same as the one in the analyzer tests' <c>PerformanceTests</c>.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
internal sealed class SubscribingAnalyzer : DiagnosticAnalyzer
{
    /// <summary>A descriptor the analyzer never reports; an analyzer must support one.</summary>
    private static readonly DiagnosticDescriptor Unused = new("BENCH", "Unused", "Unused", "Bench", DiagnosticSeverity.Hidden, true);

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Unused];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(static start => start.RegisterOperationBlockStartAction(static block =>
        {
            block.RegisterOperationAction(
                static _ => { },
                OperationKind.Invocation,
                OperationKind.Loop,
                OperationKind.PropertyReference,
                OperationKind.Return,
                OperationKind.YieldReturn,
                OperationKind.Interpolation,
                OperationKind.Conversion,
                OperationKind.ObjectCreation);
            block.RegisterOperationBlockEndAction(static _ => { });
        }));
    }
}
