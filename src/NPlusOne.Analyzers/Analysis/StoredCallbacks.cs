using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace NPlusOne.Analyzers.Analysis;

/// <summary>
/// What a delegate local, field or auto-property can hold when it is called: the lambdas and method groups assigned to
/// it, and the compiled queries (<c>EF.CompileQuery(...)</c>, <c>EF.CompileAsyncQuery(...)</c>) it was set from, each of
/// which runs its query on every call.
/// </summary>
/// <param name="Lambdas">The lambdas assigned.</param>
/// <param name="Methods">The method groups assigned.</param>
/// <param name="CompiledQueries">The <c>EF.CompileQuery</c>/<c>CompileAsyncQuery</c> calls assigned.</param>
internal sealed record StoredCallbacks(List<IAnonymousFunctionOperation> Lambdas, List<IMethodSymbol> Methods, List<IInvocationOperation> CompiledQueries)
{
    /// <summary>
    /// The round trip a compiled query makes: the operator its lambda ends with (<c>Count</c>, <c>FirstOrDefault</c>, or the
    /// rows of a sequence), at the <c>EF.CompileQuery</c> call.
    /// </summary>
    /// <param name="compile">The <c>EF.CompileQuery</c> call.</param>
    public static Effect CompiledQueryEffect(IInvocationOperation compile)
    {
        var body = compile.Arguments.Length > 0 && Operations.AsLambda(compile.Arguments[0].Value) is { } lambda
            ? Operations.StripConversions(Operations.LambdaResult(lambda))
            : null;
        return body is IInvocationOperation last
            ? new Effect(EffectKind.Query, SourceInfo.EfQuery, last.TargetMethod.Name, compile.Syntax.GetLocation(), QueryShapes.Of(last, QueryCallKind.Execute))
            : new Effect(EffectKind.Query, SourceInfo.EfQuery, compile.TargetMethod.Name, compile.Syntax.GetLocation(), QueryShape.Declared(compile.TargetMethod.Name));
    }
}
