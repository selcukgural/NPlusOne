using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace NPlusOne.Analyzers.Analysis;

/// <summary>Reads the shape of a query from the call that runs it and the operators before it.</summary>
internal static class QueryShapes
{
    /// <summary>Executing operators that compute one value rather than return rows.</summary>
    private static readonly HashSet<string> Aggregates =
    [
        "Count", "LongCount", "Any", "All", "Sum", "Average", "Min", "Max", "Contains",
        "CountAsync", "LongCountAsync", "AnyAsync", "AllAsync", "SumAsync", "AverageAsync", "MinAsync", "MaxAsync", "ContainsAsync",
    ];

    /// <summary>The shape of the round trip an executing call makes.</summary>
    /// <param name="invocation">The executing call.</param>
    /// <param name="kind">How the call was classified.</param>
    public static QueryShape Of(IInvocationOperation invocation, QueryCallKind kind)
    {
        var name = invocation.TargetMethod.Name;
        var shapeKind = kind switch
        {
            QueryCallKind.Save => ShapeKind.Save,
            QueryCallKind.ExecuteAlways when name is "Find" or "FindAsync" => ShapeKind.Find,
            QueryCallKind.ExecuteAlways when name.StartsWith("ExecuteSql", StringComparison.Ordinal) => ShapeKind.Statement,
            QueryCallKind.ExecuteAlways => ShapeKind.ExplicitLoad,
            _ when name.StartsWith("ExecuteUpdate", StringComparison.Ordinal) || name.StartsWith("ExecuteDelete", StringComparison.Ordinal) => ShapeKind.Statement,
            _ when Aggregates.Contains(name) => ShapeKind.Aggregate,
            _ => ShapeKind.Rows,
        };

        string? keyParameter = null;
        string? keyMember = null;
        string? selector = null;
        if (shapeKind == ShapeKind.Aggregate)
        {
            foreach (var argument in invocation.Arguments)
            {
                if (Operations.AsLambda(argument.Value) is { } lambda && Operations.LambdaResult(lambda)?.Type?.SpecialType != SpecialType.System_Boolean)
                {
                    selector = lambda.Syntax.ToString();
                }
            }
        }

        bool ordered = false, bounded = false;
        IOperation? current = invocation;
        while (current is IInvocationOperation call)
        {
            switch (call.TargetMethod.Name)
            {
                case "Take" or "First" or "FirstOrDefault" or "Single" or "SingleOrDefault"
                    or "FirstAsync" or "FirstOrDefaultAsync" or "SingleAsync" or "SingleOrDefaultAsync":
                    bounded |= call.TargetMethod.Name == "Take";
                    break;
                case "OrderBy" or "OrderByDescending" or "ThenBy" or "ThenByDescending":
                    ordered = true;
                    break;
            }

            if (keyMember is null)
            {
                foreach (var argument in call.Arguments)
                {
                    if (Operations.AsLambda(argument.Value) is { } lambda && FindKey(lambda) is { } key)
                    {
                        (keyParameter, keyMember) = key;
                        break;
                    }
                }
            }

            current = Operations.StripConversions(QueryCalls.FirstArgument(call));
        }

        return new QueryShape(shapeKind, name, keyParameter, keyMember, ordered && bounded, selector);
    }

    /// <summary>
    /// The key a predicate filters on: an equality between a member of the lambda's parameter and
    /// a value that does not depend on it (<c>o =&gt; o.CustomerId == c.Id</c>).
    /// </summary>
    /// <param name="lambda">A predicate lambda.</param>
    private static (string Parameter, string Member)? FindKey(IAnonymousFunctionOperation lambda)
    {
        if (lambda.Symbol.Parameters.Length != 1)
        {
            return null;
        }

        var parameter = lambda.Symbol.Parameters[0];
        foreach (var operation in lambda.Body.Descendants())
        {
            if (operation is not IBinaryOperation { OperatorKind: BinaryOperatorKind.Equals } equality)
            {
                continue;
            }

            if (MemberOf(equality.LeftOperand, parameter) is { } left && !Uses(equality.RightOperand, parameter))
            {
                return (parameter.Name, left);
            }

            if (MemberOf(equality.RightOperand, parameter) is { } right && !Uses(equality.LeftOperand, parameter))
            {
                return (parameter.Name, right);
            }
        }

        return null;
    }

    /// <summary>The member name when <paramref name="operation"/> is <c>p.Member</c>.</summary>
    /// <param name="operation">One side of the equality.</param>
    /// <param name="parameter">The lambda parameter.</param>
    private static string? MemberOf(IOperation operation, IParameterSymbol parameter)
        => Operations.StripConversions(operation) is IPropertyReferenceOperation { Instance: IParameterReferenceOperation reference } property
           && SymbolEqualityComparer.Default.Equals(reference.Parameter, parameter)
            ? property.Property.Name
            : null;

    /// <summary>Whether an expression reads the lambda parameter.</summary>
    /// <param name="operation">The expression.</param>
    /// <param name="parameter">The lambda parameter.</param>
    private static bool Uses(IOperation operation, IParameterSymbol parameter)
    {
        if (operation is IParameterReferenceOperation direct && SymbolEqualityComparer.Default.Equals(direct.Parameter, parameter))
        {
            return true;
        }

        foreach (var descendant in operation.Descendants())
        {
            if (descendant is IParameterReferenceOperation reference && SymbolEqualityComparer.Default.Equals(reference.Parameter, parameter))
            {
                return true;
            }
        }

        return false;
    }
}
