using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace NPlusOne.Analyzers.Analysis;

/// <summary>
/// Loops that make one round trip per iteration on purpose: batches, pages, polling, searches that
/// stop at the first answer (generate a code until it is unique), and values loaded once and kept.
/// Reporting these would teach users to ignore the analyzer.
/// </summary>
internal static class Exemptions
{
    /// <summary>Whether a round trip at <paramref name="operation"/> inside <paramref name="iteration"/> is intended.</summary>
    /// <param name="operation">The executing call, the call that reaches one, or a <c>foreach</c>.</param>
    /// <param name="iteration">The iteration it runs in.</param>
    /// <param name="shape">The shape of the round trip.</param>
    public static bool IsIntended(IOperation operation, Iteration iteration, QueryShape shape)
    {
        if (iteration.IsBatched || IsLoadOnce(operation) || HandlesConflictEntries(iteration.Region))
        {
            return true;
        }

        if (iteration.Region is IForEachLoopOperation or not ILoopOperation)
        {
            return false;
        }

        // A for/while/do loop that pages through ordered results (or drains a table page by page), waits between
        // attempts, retries a failed round trip, or searches until the query answers (and does not walk a collection
        // meanwhile).
        var loop = (ILoopOperation)iteration.Region;
        return shape.IsPaged || ReadsPages(loop) || Waits(loop) || IsRetry(operation, loop) || IsSearch(operation, loop);
    }

    /// <summary>
    /// Whether a loop goes over the entries of a failed save (<c>DbUpdateConcurrencyException.Entries</c>), the way EF
    /// Core's documentation resolves a concurrency conflict: the conflicting entries, not the elements of a result.
    /// </summary>
    /// <param name="region">The loop or callback.</param>
    private static bool HandlesConflictEntries(IOperation region)
        => region is IForEachLoopOperation loop
           && Operations.StripConversions(loop.Collection) is IPropertyReferenceOperation { Property.Name: "Entries", Instance.Type: { } exception }
           && DerivesFrom(exception, "DbUpdateException");

    /// <summary>
    /// Whether a for, while or do loop reads a page per iteration (<c>Take</c> on a query in its body): a loop that drains
    /// or pages through a table makes its round trips per page, saves included.
    /// </summary>
    /// <param name="loop">The loop.</param>
    private static bool ReadsPages(ILoopOperation loop)
    {
        foreach (var descendant in loop.Body.Descendants())
        {
            if (descendant is IInvocationOperation { TargetMethod: { Name: "Take", ContainingType.Name: "Queryable" } })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a round trip is retried: it runs inside a <c>try</c> with a <c>catch</c>, in a for, while or do loop that
    /// walks no collection (<c>while (!saved)</c>, <c>for (attempt &lt; 3)</c>). The loop repeats the same work until it
    /// succeeds; a loop over elements with a <c>try</c> in it is still a loop over elements.
    /// </summary>
    /// <param name="operation">The round trip.</param>
    /// <param name="loop">The loop.</param>
    private static bool IsRetry(IOperation operation, ILoopOperation loop)
    {
        if (WalksCollection(loop))
        {
            return false;
        }

        for (var current = operation.Parent; current is not null && current != loop; current = current.Parent)
        {
            if (current is ITryOperation { Catches.Length: > 0 } attempt && Operations.IsWithin(operation, attempt.Body))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a type is, or derives from, a type of that name.</summary>
    /// <param name="type">The type.</param>
    /// <param name="name">The base type's simple name.</param>
    private static bool DerivesFrom(ITypeSymbol type, string name)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.Name == name)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a loop searches rather than iterates: it walks no collection, and leaves as soon as the
    /// round trip answers, either because the query is the loop condition (<c>do { ... } while (await
    /// AnyAsync(...))</c>) or because an <c>if</c> on its result returns or breaks.
    /// </summary>
    /// <param name="operation">The round trip.</param>
    /// <param name="loop">The for, while or do loop.</param>
    private static bool IsSearch(IOperation operation, ILoopOperation loop)
    {
        if (WalksCollection(loop))
        {
            return false;
        }

        if (loop is IWhileLoopOperation { Condition: { } condition } && Operations.IsWithin(operation, condition))
        {
            return true;
        }

        var result = ResultLocal(operation);
        foreach (var descendant in loop.Body.Descendants())
        {
            if (descendant is IConditionalOperation conditional
                && (Operations.IsWithin(operation, conditional.Condition) || (result is not null && Reads(conditional.Condition, result)))
                && LeavesLoop(conditional, loop))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a loop walks a collection: its condition reads a length or count, or its body takes
    /// items from a queue, a stack or an enumerator.
    /// </summary>
    /// <param name="loop">The loop.</param>
    private static bool WalksCollection(ILoopOperation loop)
    {
        var condition = loop switch
        {
            IForLoopOperation forLoop => forLoop.Condition,
            IWhileLoopOperation whileLoop => whileLoop.Condition,
            _ => null,
        };

        if (condition is not null)
        {
            foreach (var descendant in condition.DescendantsAndSelf())
            {
                if (descendant is IPropertyReferenceOperation { Property.Name: "Length" or "Count" or "LongLength" or "IsEmpty" }
                    or IInvocationOperation { TargetMethod.Name: "Count" or "Any" or "MoveNext" or "TryDequeue" or "TryPop" or "TryTake" or "TryRead" })
                {
                    return true;
                }
            }
        }

        foreach (var descendant in loop.Body.Descendants())
        {
            // Take from a BlockingCollection, not LINQ's Take, which pages.
            if (descendant is IInvocationOperation { TargetMethod.Name: "Dequeue" or "Pop" or "MoveNext" or "TryDequeue" or "TryPop" or "TryTake" or "ReadLine" }
                or IInvocationOperation { TargetMethod: { Name: "Take", ContainingType.Name: not ("Queryable" or "Enumerable" or "AsyncEnumerable") } })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The local the round trip's result is stored in (<c>var exists = await ...</c>), if any.</summary>
    /// <param name="operation">The round trip.</param>
    private static ILocalSymbol? ResultLocal(IOperation operation)
    {
        var current = operation;
        while (current.Parent is IAwaitOperation or IConversionOperation)
        {
            current = current.Parent;
        }

        return current.Parent switch
        {
            IVariableInitializerOperation { Parent: IVariableDeclaratorOperation declarator } => declarator.Symbol,
            ISimpleAssignmentOperation { Target: ILocalReferenceOperation target } => target.Local,
            _ => null,
        };
    }

    /// <summary>Whether an expression reads the local.</summary>
    /// <param name="operation">The expression.</param>
    /// <param name="local">The local.</param>
    private static bool Reads(IOperation operation, ILocalSymbol local)
    {
        foreach (var descendant in operation.DescendantsAndSelf())
        {
            if (descendant is ILocalReferenceOperation reference && SymbolEqualityComparer.Default.Equals(reference.Local, local))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a branch of the <c>if</c> returns from the method or breaks out of this loop.</summary>
    /// <param name="conditional">The <c>if</c>.</param>
    /// <param name="loop">The loop.</param>
    private static bool LeavesLoop(IConditionalOperation conditional, ILoopOperation loop)
    {
        foreach (var branch in new[] { conditional.WhenTrue, conditional.WhenFalse })
        {
            if (branch is null)
            {
                continue;
            }

            foreach (var descendant in branch.DescendantsAndSelf())
            {
                if (descendant is IReturnOperation { Kind: OperationKind.Return }
                    || (descendant is IBranchOperation { BranchKind: BranchKind.Break } jump && SymbolEqualityComparer.Default.Equals(jump.Target, loop.ExitLabel)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the round trip only runs while a value is missing: <c>x ??= query</c>, or
    /// <c>if (x is null) { x = query; }</c>. It runs once, not once per element.
    /// </summary>
    /// <param name="operation">The executing call.</param>
    public static bool IsLoadOnce(IOperation operation)
    {
        for (var current = operation; current.Parent is { } parent; current = parent)
        {
            switch (parent)
            {
                case ICoalesceAssignmentOperation coalesce when coalesce.Value == current:
                    return true;
                case ISimpleAssignmentOperation assignment when assignment.Value == current && GuardedByNullCheck(assignment):
                    return true;
                case ILoopOperation or IAnonymousFunctionOperation or ILocalFunctionOperation:
                    return false;
            }
        }

        return false;
    }

    /// <summary>Whether an assignment sits in the true branch of a null check of the same target.</summary>
    /// <param name="assignment">The assignment.</param>
    private static bool GuardedByNullCheck(ISimpleAssignmentOperation assignment)
    {
        for (var current = (IOperation)assignment; current.Parent is { } parent; current = parent)
        {
            if (parent is IConditionalOperation conditional && conditional.WhenTrue == current)
            {
                return ChecksNull(conditional.Condition, assignment.Target);
            }

            if (parent is ILoopOperation or IAnonymousFunctionOperation)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>Whether a condition is <c>target is null</c> or <c>target == null</c>.</summary>
    /// <param name="condition">The condition.</param>
    /// <param name="target">The assigned field or local.</param>
    private static bool ChecksNull(IOperation condition, IOperation target)
    {
        var checkedOperand = Operations.StripConversions(condition) switch
        {
            IIsPatternOperation { Pattern: IConstantPatternOperation { Value: var constant } } pattern when IsNull(constant) => pattern.Value,
            IBinaryOperation { OperatorKind: BinaryOperatorKind.Equals } binary when IsNull(binary.RightOperand) => binary.LeftOperand,
            IBinaryOperation { OperatorKind: BinaryOperatorKind.Equals } binary when IsNull(binary.LeftOperand) => binary.RightOperand,
            _ => null,
        };

        return checkedOperand is not null && SameStorage(Operations.StripConversions(checkedOperand), target);
    }

    /// <summary>Whether an operation is the <c>null</c> literal.</summary>
    /// <param name="operation">The operation.</param>
    private static bool IsNull(IOperation operation)
        => Operations.StripConversions(operation) is ILiteralOperation { ConstantValue: { HasValue: true, Value: null } };

    /// <summary>Whether two operations name the same field, property or local.</summary>
    /// <param name="left">One reference.</param>
    /// <param name="right">The other.</param>
    private static bool SameStorage(IOperation? left, IOperation right)
        => (left, right) switch
        {
            (IFieldReferenceOperation a, IFieldReferenceOperation b) => SymbolEqualityComparer.Default.Equals(a.Field, b.Field),
            (IPropertyReferenceOperation a, IPropertyReferenceOperation b) => SymbolEqualityComparer.Default.Equals(a.Property, b.Property),
            (ILocalReferenceOperation a, ILocalReferenceOperation b) => SymbolEqualityComparer.Default.Equals(a.Local, b.Local),
            _ => false,
        };

    /// <summary>
    /// Whether a loop waits between iterations (<c>Task.Delay</c>, <c>Thread.Sleep</c>, a <c>PeriodicTimer</c>'s
    /// <c>WaitForNextTickAsync</c>): it polls.
    /// </summary>
    /// <param name="loop">The loop.</param>
    private static bool Waits(IOperation loop)
    {
        foreach (var descendant in loop.Descendants())
        {
            if (descendant is IInvocationOperation { TargetMethod: { Name: "Delay" or "Sleep", ContainingType.Name: "Task" or "Thread" } }
                or IInvocationOperation { TargetMethod: { Name: "WaitForNextTickAsync", ContainingType.Name: "PeriodicTimer" } })
            {
                return true;
            }
        }

        return false;
    }
}
