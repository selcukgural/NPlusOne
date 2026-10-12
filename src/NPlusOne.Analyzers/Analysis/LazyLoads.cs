using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using NPlusOne.Analyzers.Model;

namespace NPlusOne.Analyzers.Analysis;

/// <summary>An entity that is one element of an iteration, and the variable it is reached from.</summary>
/// <param name="Iteration">The loop or callback the element belongs to.</param>
/// <param name="Root">The loop variable or lambda parameter the element is reached from, for deduplication.</param>
internal sealed record PerElement(Iteration Iteration, ISymbol Root);

/// <summary>
/// Decides whether an expression is an element of an iteration: a <c>foreach</c> variable, the
/// element parameter of a per-element lambda, a list or array read at a <c>for</c> loop's index, a part of
/// a tuple or key/value pair element built from the element, a navigation read on one, or a local copied from
/// one. Only such reads can be lazy loads once per element; a read on a single entity is one query.
/// </summary>
/// <param name="session">The compilation-wide analysis state.</param>
internal sealed class LazyLoads(Session session)
{
    private readonly IterationFinder _iterations = new(session);

    /// <summary>
    /// Whether a navigation reference should not be reported on its own: it is assigned rather than
    /// read, or a longer navigation path continues from it (<c>o.Customer.Address</c> is reported once,
    /// at its end).
    /// </summary>
    /// <param name="property">The navigation reference.</param>
    /// <param name="entities">The entity model.</param>
    public static bool IsWriteOrLongerPath(IPropertyReferenceOperation property, EntityModel entities)
    {
        return property.Parent switch
        {
            ISimpleAssignmentOperation assignment => assignment.Target == property,
            IPropertyReferenceOperation outer => outer.Instance == property && entities.AsNavigation(outer.Property) is { IsLazy: true },
            INameOfOperation => true,
            _ => false,
        };
    }

    /// <summary>The iteration an entity expression is one element of, or null when it is not one.</summary>
    /// <param name="operation">The expression a navigation is read on.</param>
    public PerElement? ElementOf(IOperation? operation) => ElementOf(operation, 0);

    /// <summary>Searches with a bound on how many local copies are followed.</summary>
    /// <param name="operation">The expression.</param>
    /// <param name="hops">How many locals have been followed.</param>
    private PerElement? ElementOf(IOperation? operation, int hops)
    {
        return Operations.StripConversions(operation) switch
        {
            IPropertyReferenceOperation property when session.Entities.AsNavigation(property.Property) is not null => ElementOf(property.Instance, hops),
            IPropertyReferenceOperation { Property.IsIndexer: true, Instance: { } list, Arguments.Length: 1 } indexer => ElementAtLoopIndex(indexer, list, indexer.Arguments[0].Value),
            IArrayElementReferenceOperation { Indices.Length: 1 } element => ElementAtLoopIndex(element, element.ArrayReference, element.Indices[0]),
            IParameterReferenceOperation parameter => ElementOfLambdaParameter(parameter),
            IInstanceReferenceOperation input when Operations.PatternInput(input) is { } patterned => ElementOf(patterned, hops),
            ILocalReferenceOperation local when hops < 4 => ElementOfLocal(local, hops),
            { } other when hops < 4 && ElementParts.MemberOf(other) is { } part => ElementOfPart(part.Instance, part.Member, hops),
            _ => null,
        };
    }

    /// <summary>
    /// A list, array or dictionary read inside a <c>for</c> loop with an index built from the loop's own counter
    /// (<c>orders[i]</c>, <c>orders[orders.Count - 1 - i]</c>) is a different element on every iteration. An index
    /// that does not use the counter (<c>orders[0]</c>) reads the same element each time, which loads once.
    /// </summary>
    /// <param name="reference">The indexer or array element reference.</param>
    /// <param name="collection">The list or array read.</param>
    /// <param name="index">The index.</param>
    private PerElement? ElementAtLoopIndex(IOperation reference, IOperation collection, IOperation index)
    {
        var current = reference;
        while (current.Parent is { } parent)
        {
            if (parent is IForLoopOperation loop && current == loop.Body && SteppedCounter(index, loop) is { } counter)
            {
                var iteration = _iterations.Find(loop.Body) is { } found && found.Region == loop
                    ? found
                    : new Iteration(IterationKind.Loop, loop, "the for loop", false);
                return new PerElement(iteration, Symbol(collection) ?? counter);
            }

            if (parent is IWhileLoopOperation whileLoop && current == whileLoop.Body && SteppedInBody(index, whileLoop) is { } stepped)
            {
                var iteration = _iterations.Find(whileLoop.Body) is { } found && found.Region == whileLoop
                    ? found
                    : new Iteration(IterationKind.Loop, whileLoop, "the while loop", false);
                return new PerElement(iteration, Symbol(collection) ?? stepped);
            }

            current = parent;
        }

        return null;
    }

    /// <summary>A local the <c>for</c> loop declares and steps at the bottom of each iteration that the index uses, or null.</summary>
    /// <param name="index">The index expression.</param>
    /// <param name="loop">The loop.</param>
    private static ILocalSymbol? SteppedCounter(IOperation index, IForLoopOperation loop)
    {
        foreach (var operation in index.DescendantsAndSelf())
        {
            if (operation is not ILocalReferenceOperation reference || !loop.Locals.Contains(reference.Local, SymbolEqualityComparer.Default))
            {
                continue;
            }

            foreach (var step in loop.AtLoopBottom.SelectMany(static s => s.DescendantsAndSelf()))
            {
                var target = step switch
                {
                    IIncrementOrDecrementOperation increment => increment.Target,
                    IAssignmentOperation assignment => assignment.Target,
                    _ => null,
                };
                if (target is ILocalReferenceOperation stepped && SymbolEqualityComparer.Default.Equals(stepped.Local, reference.Local))
                {
                    return reference.Local;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// A local declared before a <c>while</c> loop that the index uses and the loop body steps (<c>i++</c>, <c>i += 1</c>),
    /// so the index moves on every iteration; null otherwise.
    /// </summary>
    /// <param name="index">The index expression.</param>
    /// <param name="loop">The while loop.</param>
    private static ILocalSymbol? SteppedInBody(IOperation index, IWhileLoopOperation loop)
    {
        foreach (var operation in index.DescendantsAndSelf())
        {
            if (operation is not ILocalReferenceOperation reference || loop.Locals.Contains(reference.Local, SymbolEqualityComparer.Default))
            {
                continue;
            }

            foreach (var step in loop.Body.DescendantsAndSelf())
            {
                var target = step switch
                {
                    IIncrementOrDecrementOperation increment => increment.Target,
                    ICompoundAssignmentOperation assignment => assignment.Target,
                    _ => null,
                };
                if (target is ILocalReferenceOperation stepped && SymbolEqualityComparer.Default.Equals(stepped.Local, reference.Local))
                {
                    return reference.Local;
                }
            }
        }

        return null;
    }

    /// <summary>The variable, parameter, field or property an expression reads, used to tell elements of different lists apart.</summary>
    /// <param name="operation">The expression.</param>
    private static ISymbol? Symbol(IOperation operation)
    {
        return Operations.StripConversions(operation) switch
        {
            ILocalReferenceOperation local => local.Local,
            IParameterReferenceOperation parameter => parameter.Parameter,
            IFieldReferenceOperation field => field.Field,
            IPropertyReferenceOperation property => property.Property,
            _ => null,
        };
    }

    /// <summary>
    /// A part of a <c>foreach</c> element or of a lambda's element (<c>pair.Order</c>, <c>pair.Value</c>,
    /// <c>pairs.Sum(p =&gt; p.Order...)</c>) is an element when the part was built from the element each time, not from
    /// something shared by all of them.
    /// </summary>
    /// <param name="instance">The element the part is read from.</param>
    /// <param name="member">The part.</param>
    /// <param name="hops">How many locals have been followed.</param>
    private PerElement? ElementOfPart(IOperation instance, string member, int hops)
    {
        if (Operations.StripConversions(instance) is IParameterReferenceOperation parameter)
        {
            return ElementOfLambdaParameter(parameter) is { Iteration.Region: IAnonymousFunctionOperation lambda } element
                   && CallbackSite.Of(lambda) is { } site
                   && SourceTracer.SequenceArgumentOf(site.Invocation, session.Symbols) is { } sequence
                   && IsBuiltPerElement(ElementParts.Resolve(sequence, member), hops)
                ? element
                : null;
        }

        if (Operations.StripConversions(instance) is not ILocalReferenceOperation local
            || ElementParts.LoopDeclaring(local.Local, local) is not { } loop
            || !IsBuiltPerElement(ElementParts.Resolve(loop.Collection, member), hops))
        {
            return null;
        }

        return new PerElement(ForEachIteration(loop), local.Local);
    }

    /// <summary>Whether a part is an element of a sequence, or built from the element of the lambda that builds each one.</summary>
    /// <param name="part">The part, null when not known.</param>
    /// <param name="hops">How many locals have been followed.</param>
    private bool IsBuiltPerElement(ElementPart? part, int hops)
        => part is { ElementOf: not null } || part?.Value is { } value && ElementOf(value, hops + 1) is not null;

    /// <summary>The iteration a <c>foreach</c> loop is.</summary>
    /// <param name="loop">The loop.</param>
    private Iteration ForEachIteration(IForEachLoopOperation loop)
        => _iterations.Find(loop.Body) is { } found && found.Region == loop
            ? found
            : new Iteration(IterationKind.Loop, loop, $"the foreach loop over '{Operations.Describe(loop.Collection)}'", false);

    /// <summary>A lambda parameter is an element when the lambda is called once per element of a sequence.</summary>
    /// <param name="reference">The parameter reference.</param>
    private PerElement? ElementOfLambdaParameter(IParameterReferenceOperation reference)
    {
        if (reference.Parameter.ContainingSymbol is not IMethodSymbol { MethodKind: MethodKind.AnonymousFunction } lambdaSymbol)
        {
            return null;
        }

        for (var current = reference.Parent; current is not null; current = current.Parent)
        {
            if (current is not IAnonymousFunctionOperation lambda || !SymbolEqualityComparer.Default.Equals(lambda.Symbol, lambdaSymbol))
            {
                continue;
            }

            if (Operations.IsExpressionTree(lambda) || CallbackSite.Of(lambda) is not { } site || !session.IsIterating(site.Invocation.TargetMethod, site.Parameter))
            {
                return null;
            }

            var iteration = new Iteration(IterationKind.Callback, lambda, $"the '{site.Invocation.TargetMethod.Name}' lambda", false);
            return new PerElement(iteration, reference.Parameter);
        }

        return null;
    }

    /// <summary>
    /// A local is an element when it is a <c>foreach</c> variable, a part the loop deconstructs that was built from
    /// the element, or declared once from an element (<c>var customer = order.Customer;</c>).
    /// </summary>
    /// <param name="reference">The local reference.</param>
    /// <param name="hops">How many locals have been followed.</param>
    private PerElement? ElementOfLocal(ILocalReferenceOperation reference, int hops)
    {
        for (var current = reference.Parent; current is not null; current = current.Parent)
        {
            if (current is not IForEachLoopOperation loop || !loop.Locals.Contains(reference.Local, SymbolEqualityComparer.Default))
            {
                continue;
            }

            if (loop.LoopControlVariable is IVariableDeclaratorOperation)
            {
                return new PerElement(ForEachIteration(loop), reference.Local);
            }

            return ElementParts.DeconstructedMember(loop, reference.Local, session.Symbols) is { } member
                   && IsBuiltPerElement(ElementParts.Resolve(loop.Collection, member), hops)
                ? new PerElement(ForEachIteration(loop), reference.Local)
                : null;
        }

        return Operations.SingleInitializer(reference) is { } initializer ? ElementOf(initializer, hops + 1) : null;
    }
}
