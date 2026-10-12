using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using NPlusOne.Analyzers.Analysis;
using NPlusOne.Analyzers.Model;

namespace NPlusOne.Analyzers;

/// <summary>
/// The N+1 analyzer. It does nothing in a compilation that does not reference EF Core; otherwise it
/// builds one <see cref="Session"/> per compilation and analyses every operation block with a
/// <see cref="BlockAnalyzer"/>. See docs/design.md for what each rule proves.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NPlusOneAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        Descriptors.QueryPerElement,
        Descriptors.LazyLoadPerElement,
        Descriptors.SavePerElement,
        Descriptors.RecursiveQuery,
        Descriptors.SerializedLazyEntities,
        Descriptors.PossibleQueryPerElement,
        Descriptors.ConventionMappingPerElement);

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    /// <summary>Sets up the session and the per-block analysis for a compilation that uses EF Core.</summary>
    /// <param name="context">The compilation start context.</param>
    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        if (KnownSymbols.TryCreate(context.Compilation) is not { } symbols)
        {
            return;
        }

        var session = new Session(context.Compilation, symbols, Settings.Read(context.Options.AnalyzerConfigOptionsProvider, context.Compilation));
        context.RegisterOperationBlockStartAction(block =>
        {
            var analyzer = new BlockAnalyzer(session, block.OwningSymbol);
            block.RegisterOperationAction(analyzer.OnInvocation, OperationKind.Invocation);
            block.RegisterOperationAction(analyzer.OnForEach, OperationKind.Loop);
            block.RegisterOperationAction(analyzer.OnPropertyReference, OperationKind.PropertyReference);
            block.RegisterOperationAction(analyzer.OnReturn, OperationKind.Return, OperationKind.YieldReturn);
            block.RegisterOperationAction(analyzer.OnInterpolation, OperationKind.Interpolation);
            block.RegisterOperationAction(analyzer.OnConversion, OperationKind.Conversion);
            block.RegisterOperationAction(analyzer.OnObjectCreation, OperationKind.ObjectCreation);
            block.RegisterOperationBlockEndAction(analyzer.OnBlockEnd);
        });
    }
}
