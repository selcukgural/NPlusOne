using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace NPlusOne.Analyzers.Analysis;

/// <summary>Builds the summary of one method from its body; see <see cref="MethodSummary"/>.</summary>
/// <param name="session">The compilation-wide analysis state.</param>
/// <param name="body">The method's body: a method body, a local function or an expression.</param>
/// <param name="method">The method being summarised.</param>
internal sealed class SummaryBuilder(Session session, IOperation body, IMethodSymbol method)
{
    /// <summary>Lambdas whose summary is being built on this thread, so a lambda that calls itself through its local stops.</summary>
    [ThreadStatic]
    private static HashSet<IOperation>? _lambdasInProgress;

    private readonly SourceTracer _tracer = new(session, method);
    private readonly IterationFinder _iterations = new(session);
    private readonly LazyLoads _lazy = new(session);
    private readonly ImmutableArray<Effect>.Builder _effects = ImmutableArray.CreateBuilder<Effect>();
    private readonly ImmutableArray<CallEdge>.Builder _calls = ImmutableArray.CreateBuilder<CallEdge>();
    private readonly ImmutableHashSet<int>.Builder _delegates = ImmutableHashSet.CreateBuilder<int>();
    private readonly ImmutableArray<ParameterNavigationRead>.Builder _reads = ImmutableArray.CreateBuilder<ParameterNavigationRead>();
    private SourceInfo _returns = SourceInfo.Pending;

    /// <summary>Walks the body once and returns the summary.</summary>
    public MethodSummary Build()
    {
        foreach (var operation in body.DescendantsAndSelf())
        {
            if (!RunsWhenCalled(operation))
            {
                continue;
            }

            switch (operation)
            {
                case IInvocationOperation invocation:
                    VisitInvocation(invocation);
                    break;
                case IForEachLoopOperation loop:
                    VisitForEach(loop);
                    break;
                case IPropertyReferenceOperation property:
                    VisitProperty(property);
                    break;
                case IReturnOperation { ReturnedValue: { } value } returned when BelongsToMethod(returned) && ReturnsSomethingTraceable():
                    _returns = _returns.Combine(_tracer.Trace(value));
                    break;
            }
        }

        return new MethodSummary(
            _effects.ToImmutable(),
            _calls.ToImmutable(),
            _delegates.ToImmutable(),
            _reads.ToImmutable(),
            _returns.Kind == OriginKind.Pending ? SourceInfo.Unknown : _returns);
    }

    /// <summary>
    /// Whether code at this operation runs when the method is called: not inside an expression
    /// tree (translated to SQL) and not inside a lambda that is only stored for later.
    /// </summary>
    /// <param name="operation">An operation of the body.</param>
    private bool RunsWhenCalled(IOperation operation)
    {
        for (var current = operation.Parent; current is not null && current != body; current = current.Parent)
        {
            if (current is not IAnonymousFunctionOperation lambda)
            {
                continue;
            }

            if (Operations.IsExpressionTree(lambda) || CallbackSite.Of(lambda) is null)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Records the round trip, the call or the delegate invocation an invocation is.</summary>
    /// <param name="invocation">The invocation.</param>
    private void VisitInvocation(IInvocationOperation invocation)
    {
        VisitCallbackArguments(invocation);
        var call = QueryCalls.Classify(invocation, session.Symbols);
        switch (call.Kind)
        {
            case QueryCallKind.Execute:
                {
                    var source = _tracer.Trace(call.Source);
                    if ((source.IsEfQuery || source.Kind == OriginKind.Parameter) && !Exemptions.IsLoadOnce(invocation))
                    {
                        _effects.Add(new Effect(EffectKind.Query, source, invocation.TargetMethod.Name, Operations.ReportLocation(invocation), QueryShapes.Of(invocation, call.Kind)));
                    }

                    return;
                }

            case QueryCallKind.ExecuteAlways when !Exemptions.IsLoadOnce(invocation):
                _effects.Add(new Effect(EffectKind.Query, SourceInfo.EfQuery, invocation.TargetMethod.Name, Operations.ReportLocation(invocation), QueryShapes.Of(invocation, call.Kind)));
                return;
            case QueryCallKind.Save:
                _effects.Add(new Effect(EffectKind.Save, SourceInfo.EfQuery, invocation.TargetMethod.Name, Operations.ReportLocation(invocation), QueryShapes.Of(invocation, call.Kind)));
                return;
            case QueryCallKind.None:
                break;
            default:
                return;
        }

        if (invocation.TargetMethod.MethodKind == MethodKind.DelegateInvoke)
        {
            RecordDelegateInvocation(invocation);
            RecordStoredCallbacks(invocation);
            return;
        }

        if (!Exemptions.IsLoadOnce(invocation))
        {
            _calls.Add(new CallEdge(invocation.TargetMethod.OriginalDefinition, TraceArguments(invocation), Operations.ReportLocation(invocation)));
            foreach (var handler in session.MediatorHandlers(invocation))
            {
                _calls.Add(new CallEdge(handler, ImmutableArray<SourceInfo>.Empty, Operations.ReportLocation(invocation)));
            }
        }
    }

    /// <summary>
    /// Records what a call's delegate arguments do per element: a method group passed to an
    /// iterating method is a call made once per element (with an element as its argument), and a
    /// delegate parameter of this method handed to one is invoked per element.
    /// </summary>
    /// <param name="invocation">The call, whatever it does to queries.</param>
    private void VisitCallbackArguments(IInvocationOperation invocation)
    {
        foreach (var argument in invocation.Arguments)
        {
            if (argument.Parameter is null)
            {
                continue;
            }

            // A method group passed to an iterating method is called with each element.
            if (Operations.AsMethodGroup(argument.Value) is { } group && session.IsIterating(invocation.TargetMethod, argument.Parameter))
            {
                var element = _tracer.SequenceArgument(invocation) is { } sequence ? _tracer.Trace(sequence).Element() : SourceInfo.Unknown;
                _calls.Add(new CallEdge(group.OriginalDefinition, ImmutableArray.Create(element), Operations.ReportLocation(argument.Value)));
            }

            // A delegate parameter of this method handed on to an iterating method is called per element too.
            if (Operations.StripConversions(argument.Value) is IParameterReferenceOperation { Parameter: var passed }
                && IsOwnParameter(passed)
                && session.IsIterating(invocation.TargetMethod, argument.Parameter))
            {
                _delegates.Add(passed.Ordinal);
            }
        }
    }

    /// <summary>Records a delegate parameter called inside a loop or per-element callback.</summary>
    /// <param name="invocation">The delegate invocation.</param>
    private void RecordDelegateInvocation(IInvocationOperation invocation)
    {
        if (Operations.StripConversions(invocation.Instance) is IParameterReferenceOperation { Parameter: var parameter }
            && IsOwnParameter(parameter)
            && _iterations.Find(invocation) is { Kind: not IterationKind.ExpressionTree })
        {
            _delegates.Add(parameter.Ordinal);
        }
    }

    /// <summary>
    /// A call through a delegate local, field or auto-property runs what was assigned to it: the round trips of its
    /// lambdas become the method's own, its method groups become calls, and a compiled query is a query.
    /// </summary>
    /// <param name="invocation">The delegate invocation.</param>
    private void RecordStoredCallbacks(IInvocationOperation invocation)
    {
        var (lambdas, methods, compiled) = session.CallbacksOf(invocation.Instance);
        foreach (var compile in compiled)
        {
            _effects.Add(StoredCallbacks.CompiledQueryEffect(compile));
        }

        var inProgress = _lambdasInProgress ??= [];
        foreach (var lambda in lambdas)
        {
            if (!inProgress.Add(lambda))
            {
                continue;
            }

            try
            {
                // The lambda's parameters are not this method's: only what does not depend on them carries over.
                var inner = new SummaryBuilder(session, lambda.Body, lambda.Symbol).Build();
                foreach (var effect in inner.Effects)
                {
                    if (effect.Source.Kind == OriginKind.Ef)
                    {
                        _effects.Add(effect);
                    }
                }

                foreach (var call in inner.Calls)
                {
                    _calls.Add(call with { Arguments = WithoutParameters(call.Arguments) });
                }
            }
            finally
            {
                inProgress.Remove(lambda);
            }
        }

        foreach (var target in methods)
        {
            _calls.Add(new CallEdge(target.OriginalDefinition, TraceArguments(invocation), Operations.ReportLocation(invocation)));
        }
    }

    /// <summary>A <c>foreach</c> over an EF query runs it.</summary>
    /// <param name="loop">The loop.</param>
    private void VisitForEach(IForEachLoopOperation loop)
    {
        var source = _tracer.Trace(loop.Collection);
        if (source.IsEfQuery || source.Kind == OriginKind.Parameter)
        {
            _effects.Add(new Effect(EffectKind.Query, source, "foreach", loop.Collection.Syntax.GetLocation(), new QueryShape(ShapeKind.Rows, "foreach", null, null, false)));
        }
    }

    /// <summary>
    /// Records the getter a property read runs, as a call (a getter or indexer that queries makes the method query),
    /// and lazy navigations read on parameters, for mappers called once per element.
    /// </summary>
    /// <param name="property">The property reference.</param>
    private void VisitProperty(IPropertyReferenceOperation property)
    {
        if (BlockAnalyzer.RunGetter(property, session) is { } getter)
        {
            _calls.Add(new CallEdge(getter.OriginalDefinition, TraceIndexerArguments(property, getter), property.Syntax.GetLocation()));
        }

        if (session.Entities.AsNavigation(property.Property) is not { IsLazy: true }
            || LazyLoads.IsWriteOrLongerPath(property, session.Entities))
        {
            return;
        }

        var traced = _tracer.Trace(property);
        if (traced.Kind == OriginKind.Parameter && IsOwnParameterOrdinal(traced.Parameter))
        {
            // Read on an element of a loop or callback over the parameter, or on the parameter itself.
            var perElement = traced.Parameter != ParameterNavigationRead.This && _lazy.ElementOf(property.Instance) is not null;
            _reads.Add(new ParameterNavigationRead(traced.Parameter, traced.Path, perElement));
        }
    }

    /// <summary>Traces an indexer's arguments by its getter's parameter ordinals; empty for a plain property.</summary>
    /// <param name="property">The property or indexer read.</param>
    /// <param name="getter">Its getter.</param>
    private ImmutableArray<SourceInfo> TraceIndexerArguments(IPropertyReferenceOperation property, IMethodSymbol getter)
    {
        var builder = ImmutableArray.CreateBuilder<SourceInfo>(getter.Parameters.Length);
        for (var i = 0; i < getter.Parameters.Length; i++)
        {
            builder.Add(i < property.Arguments.Length && MayCarryQuery(getter.Parameters[i].Type) ? _tracer.Trace(property.Arguments[i].Value) : SourceInfo.Unknown);
        }

        return builder.MoveToImmutable();
    }

    /// <summary>Traces the arguments of a call by the callee's parameter ordinals.</summary>
    /// <param name="invocation">The call.</param>
    private ImmutableArray<SourceInfo> TraceArguments(IInvocationOperation invocation)
    {
        var parameters = invocation.TargetMethod.Parameters;
        var builder = ImmutableArray.CreateBuilder<SourceInfo>(parameters.Length);
        for (var i = 0; i < parameters.Length; i++)
        {
            builder.Add(SourceInfo.Unknown);
        }

        foreach (var argument in invocation.Arguments)
        {
            if (argument.Parameter is { } parameter && parameter.Ordinal < builder.Count && MayCarryQuery(parameter.Type))
            {
                builder[parameter.Ordinal] = _tracer.Trace(argument.Value);
            }
        }

        return builder.MoveToImmutable();
    }

    /// <summary>Replaces parameter sources (of a lambda) with unknown ones.</summary>
    /// <param name="arguments">The arguments of a call made inside a lambda.</param>
    private static ImmutableArray<SourceInfo> WithoutParameters(ImmutableArray<SourceInfo> arguments)
    {
        var builder = ImmutableArray.CreateBuilder<SourceInfo>(arguments.Length);
        foreach (var argument in arguments)
        {
            builder.Add(argument.Kind == OriginKind.Parameter ? SourceInfo.Unknown : argument);
        }

        return builder.MoveToImmutable();
    }

    /// <summary>Whether a parameter type can carry a query, a sequence or an entity (not a number or a string).</summary>
    /// <param name="type">The parameter type.</param>
    private static bool MayCarryQuery(ITypeSymbol type)
        => type.SpecialType == SpecialType.None && type.TypeKind is TypeKind.Class or TypeKind.Interface or TypeKind.TypeParameter or TypeKind.Array;

    /// <summary>Whether a return statement belongs to the method itself, not a lambda or local function in it.</summary>
    /// <param name="returned">The return.</param>
    private bool BelongsToMethod(IReturnOperation returned)
    {
        for (var current = returned.Parent; current is not null && current != body; current = current.Parent)
        {
            if (current is IAnonymousFunctionOperation or ILocalFunctionOperation)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether the method returns something tracing can say anything about.</summary>
    private bool ReturnsSomethingTraceable()
    {
        var type = session.Symbols.Unwrap(method.ReturnType);
        return type is not null && !method.ReturnsVoid && MayCarryQuery(type);
    }

    /// <summary>Whether a parameter belongs to the summarised method (not to a lambda inside it).</summary>
    /// <param name="parameter">The parameter.</param>
    private bool IsOwnParameter(IParameterSymbol parameter)
        => SymbolEqualityComparer.Default.Equals(parameter.ContainingSymbol, method) || SymbolEqualityComparer.Default.Equals(parameter.ContainingSymbol?.OriginalDefinition, method.OriginalDefinition);

    /// <summary>Whether an ordinal is in range of the method's parameters.</summary>
    /// <param name="ordinal">The ordinal.</param>
    private bool IsOwnParameterOrdinal(int ordinal)
        => (ordinal >= 0 && ordinal < method.Parameters.Length) || (ordinal == ParameterNavigationRead.This && !method.IsStatic);
}
