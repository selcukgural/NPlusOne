using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace NPlusOne.Analyzers.Analysis;

/// <summary>
/// Finds what makes an operation run once per element inside its own method: the innermost loop
/// whose body (not collection) contains it, or a lambda passed to a method that calls it per
/// element. A lambda passed to any other call (<c>Task.Run</c>) runs where the call runs, so the
/// search continues from the call; a local function runs where it is called.
/// </summary>
/// <param name="session">The compilation-wide analysis state, which knows which methods iterate.</param>
internal sealed class IterationFinder(Session session)
{
    /// <summary>
    /// The innermost iteration around <paramref name="operation"/>, <see cref="Iteration.InExpressionTree"/>
    /// when it is translated into SQL, or null when it runs once per call of its method.
    /// </summary>
    /// <param name="operation">The operation.</param>
    public Iteration? Find(IOperation operation)
    {
        var current = operation;
        while (current.Parent is { } parent)
        {
            switch (parent)
            {
                case IForEachLoopOperation loop when current != loop.Collection:
                    return ForEach(loop);
                case IForLoopOperation loop when !loop.Before.Contains(current):
                    return new Iteration(IterationKind.Loop, loop, "the for loop", false);
                case IWhileLoopOperation loop:
                    return new Iteration(IterationKind.Loop, loop, loop.ConditionIsTop ? "the while loop" : "the do-while loop", false);
                case IAnonymousFunctionOperation lambda:
                    {
                        if (Operations.IsExpressionTree(lambda))
                        {
                            return Iteration.InExpressionTree;
                        }

                        if (CallbackSite.Of(lambda) is not { } site)
                        {
                            return null;
                        }

                        if (session.IsIterating(site.Invocation.TargetMethod, site.Parameter))
                        {
                            return new Iteration(IterationKind.Callback, lambda, static region => DescribeCallback(CalledWith(region).TargetMethod), false);
                        }

                        if (IsPerItemRule(site.Invocation))
                        {
                            return new Iteration(
                                IterationKind.Callback,
                                lambda,
                                static region => $"the '{CalledWith(region).TargetMethod.Name}' rule chained on RuleForEach, which FluentValidation runs for each item",
                                false);
                        }

                        // A cache factory runs on a miss: once for a key that is the same on every element.
                        if (CacheKey(site.Invocation) is { } key)
                        {
                            var outer = Find(site.Invocation);
                            return outer is { Kind: not IterationKind.ExpressionTree } && !VariesWithin(key, outer.Region) ? null : outer;
                        }

                        current = site.Invocation;
                        continue;
                    }

                case ILocalFunctionOperation:
                    // A local function runs where it is called; calls to it are analysed as calls.
                    return null;
            }

            current = parent;
        }

        return null;
    }

    /// <summary>
    /// Whether a FluentValidation rule method (<c>Must</c>, <c>MustAsync</c>, <c>Custom</c>, <c>CustomAsync</c>) is chained
    /// on <c>RuleForEach</c>: FluentValidation calls its predicate once per item of the collection. On <c>RuleFor</c> it runs
    /// once per validated object.
    /// </summary>
    /// <param name="rule">The call the lambda is passed to.</param>
    private static bool IsPerItemRule(IInvocationOperation rule)
    {
        if (rule.TargetMethod is not { Name: "Must" or "MustAsync" or "Custom" or "CustomAsync" } method
            || method.ContainingNamespace?.ToDisplayString() != "FluentValidation")
        {
            return false;
        }

        for (var current = QueryCalls.FirstArgument(rule); Operations.StripConversions(current) is IInvocationOperation chained; current = QueryCalls.FirstArgument(chained))
        {
            if (chained.TargetMethod.Name == "RuleForEach")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The key of a call that runs its factory only when the key is missing from a cache (<c>GetOrCreate</c>,
    /// <c>GetOrCreateAsync</c> of <c>IMemoryCache</c> and <c>HybridCache</c>, <c>ConcurrentDictionary.GetOrAdd</c>,
    /// <c>GetOrSet</c>); null for any other call, or when the key argument is not named <c>key</c>.
    /// </summary>
    /// <param name="call">The call a lambda is passed to.</param>
    private static IOperation? CacheKey(IInvocationOperation call)
    {
        if (call.TargetMethod.Name is not ("GetOrCreate" or "GetOrCreateAsync" or "GetOrAdd" or "GetOrAddAsync" or "GetOrSet" or "GetOrSetAsync"))
        {
            return null;
        }

        foreach (var argument in call.Arguments)
        {
            if (argument.Parameter?.Name == "key")
            {
                return argument.Value;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a value can differ between the runs of an iteration: it reads a local or parameter declared inside it (the
    /// loop variable, a local of the loop body, the element parameter of a lambda).
    /// </summary>
    /// <param name="value">The value, such as a cache key.</param>
    /// <param name="region">The loop or lambda.</param>
    private static bool VariesWithin(IOperation value, IOperation region)
    {
        foreach (var operation in value.DescendantsAndSelf())
        {
            ISymbol? symbol = operation switch
            {
                ILocalReferenceOperation local => local.Local,
                IParameterReferenceOperation parameter => parameter.Parameter,
                _ => null,
            };
            if (symbol?.Locations.FirstOrDefault() is { IsInSource: true } location
                && location.SourceTree == region.Syntax.SyntaxTree
                && region.Syntax.Span.Contains(location.SourceSpan))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a <c>foreach</c> walks batches (<c>Chunk</c>, MoreLINQ's <c>Batch</c>).</summary>
    /// <param name="collection">The loop's collection expression.</param>
    private static bool IsBatched(IOperation collection)
        => Operations.StripConversions(collection) is IInvocationOperation { TargetMethod.Name: "Chunk" or "Batch" };

    /// <summary>The iteration a <c>foreach</c> loop is, named when a message first needs it.</summary>
    /// <param name="loop">The loop.</param>
    public static Iteration ForEach(IForEachLoopOperation loop)
        => new(IterationKind.Loop, loop, static region => DescribeForEach((IForEachLoopOperation)region), IsBatched(loop.Collection));

    /// <summary>The call a lambda is passed to, for a lambda known to be passed to one.</summary>
    /// <param name="lambda">The lambda.</param>
    internal static IInvocationOperation CalledWith(IOperation lambda) => CallbackSite.Of(lambda)!.Invocation;

    /// <summary>How messages name a <c>foreach</c> loop.</summary>
    /// <param name="loop">The loop.</param>
    private static string DescribeForEach(IForEachLoopOperation loop)
        => $"the {(loop.IsAsynchronous ? "await foreach" : "foreach")} loop over '{Operations.Describe(loop.Collection)}'";

    /// <summary>How messages name a callback: <c>the 'Select' lambda</c>, <c>the 'Parallel.ForEach' callback</c>.</summary>
    /// <param name="method">The iterating method.</param>
    private static string DescribeCallback(IMethodSymbol method)
        => method.ContainingType?.Name is "Parallel" or "Array"
            ? $"the '{method.ContainingType.Name}.{method.Name}' callback"
            : $"the '{method.Name}' lambda";
}
