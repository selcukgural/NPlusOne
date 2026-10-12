using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace NPlusOne.Analyzers.Analysis;

/// <summary>Helpers over the operation tree that every part of the analysis needs.</summary>
internal static class Operations
{
    /// <summary>Removes implicit and explicit conversions, parentheses and <c>!</c> around an operation.</summary>
    /// <param name="operation">The operation, possibly null.</param>
    public static IOperation? StripConversions(IOperation? operation)
    {
        while (true)
        {
            switch (operation)
            {
                case IConversionOperation conversion:
                    operation = conversion.Operand;
                    continue;
                case IParenthesizedOperation parenthesized:
                    operation = parenthesized.Operand;
                    continue;
                case IDelegateCreationOperation { Target: IAnonymousFunctionOperation } delegateCreation:
                    return delegateCreation.Target;
                default:
                    return operation;
            }
        }
    }

    /// <summary>The lambda an argument value holds, through conversions and delegate creation.</summary>
    /// <param name="operation">An argument value.</param>
    public static IAnonymousFunctionOperation? AsLambda(IOperation? operation)
        => StripConversions(operation) as IAnonymousFunctionOperation;

    /// <summary>The method a method group argument names (<c>Select(Mapper.Map)</c>), or null.</summary>
    /// <param name="operation">An argument value.</param>
    public static IMethodSymbol? AsMethodGroup(IOperation? operation)
    {
        var stripped = operation;
        while (stripped is IConversionOperation conversion)
        {
            stripped = conversion.Operand;
        }

        return stripped switch
        {
            IDelegateCreationOperation { Target: IMethodReferenceOperation reference } => reference.Method,
            IMethodReferenceOperation reference => reference.Method,
            _ => null,
        };
    }

    /// <summary>Every value a local is declared with or assigned in its method.</summary>
    /// <param name="local">The local.</param>
    /// <param name="anyOperation">Any operation of the method, to find its root.</param>
    public static ImmutableArray<IOperation> AssignedValues(ILocalSymbol local, IOperation anyOperation)
    {
        var values = ImmutableArray.CreateBuilder<IOperation>();
        foreach (var descendant in Root(anyOperation).Descendants())
        {
            var value = descendant switch
            {
                IVariableDeclaratorOperation declarator when SymbolEqualityComparer.Default.Equals(declarator.Symbol, local) => declarator.GetVariableInitializer()?.Value,
                ISimpleAssignmentOperation { Target: ILocalReferenceOperation target } assignment when SymbolEqualityComparer.Default.Equals(target.Local, local) => assignment.Value,
                _ => null,
            };

            if (value is not null)
            {
                values.Add(value);
            }
        }

        return values.ToImmutable();
    }

    /// <summary>
    /// What a property pattern reads its members on: for <c>d is { Floor.Name: "x" }</c>, <c>d</c>; for the inner pattern
    /// of <c>d is { Floor: { Name: "x" } }</c>, the <c>Floor</c> member of the outer one; for a <c>switch</c>, the value
    /// switched on. Null when <paramref name="operation"/> is not a pattern's input or the pattern is in an unknown place.
    /// </summary>
    /// <param name="operation">An instance a property is read on.</param>
    public static IOperation? PatternInput(IOperation? operation)
    {
        if (operation is not IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.PatternInput })
        {
            return null;
        }

        var current = operation.Parent;
        while (current is not null and not IRecursivePatternOperation)
        {
            current = current.Parent;
        }

        for (var pattern = current?.Parent; pattern is not null; pattern = pattern.Parent)
        {
            switch (pattern)
            {
                case IIsPatternOperation isPattern:
                    return isPattern.Value;
                case IPropertySubpatternOperation subpattern:
                    return subpattern.Member;
                case ISwitchExpressionArmOperation { Parent: ISwitchExpressionOperation switchExpression }:
                    return switchExpression.Value;
                case ISwitchCaseOperation { Parent: ISwitchOperation switchStatement }:
                    return switchStatement.Value;
                case IBinaryPatternOperation or INegatedPatternOperation or IPatternCaseClauseOperation or ISwitchExpressionArmOperation:
                    continue;
                default:
                    return null;
            }
        }

        return null;
    }

    /// <summary>The value an expression-bodied lambda returns (its single return), or null for a block body.</summary>
    /// <param name="lambda">The lambda.</param>
    public static IOperation? LambdaResult(IAnonymousFunctionOperation lambda)
    {
        var operations = lambda.Body.Operations;
        return operations.Length >= 1 && operations[0] is IReturnOperation { ReturnedValue: { } value } ? value : null;
    }

    /// <summary>
    /// The value a local is declared with, when that is the only assignment it ever gets in its method
    /// (<c>var pairs = orders.Select(...);</c>); null when it is assigned again or declared without a value.
    /// </summary>
    /// <param name="reference">A reference to the local.</param>
    public static IOperation? SingleInitializer(ILocalReferenceOperation reference)
    {
        IOperation? initializer = null;
        var assignments = 0;
        foreach (var descendant in Root(reference).Descendants())
        {
            switch (descendant)
            {
                case IVariableDeclaratorOperation declarator when SymbolEqualityComparer.Default.Equals(declarator.Symbol, reference.Local):
                    initializer = declarator.GetVariableInitializer()?.Value;
                    assignments++;
                    break;
                case ISimpleAssignmentOperation { Target: ILocalReferenceOperation target } when SymbolEqualityComparer.Default.Equals(target.Local, reference.Local):
                    assignments++;
                    break;
            }
        }

        return assignments == 1 ? initializer : null;
    }

    /// <summary>
    /// Whether the lambda is converted to an expression tree. EF Core translates such a lambda
    /// into SQL, so nothing inside it runs as a separate round trip.
    /// </summary>
    /// <param name="lambda">The lambda.</param>
    public static bool IsExpressionTree(IAnonymousFunctionOperation lambda)
    {
        if (lambda.SemanticModel is null)
        {
            return false;
        }

        var converted = lambda.SemanticModel.GetTypeInfo(lambda.Syntax).ConvertedType;
        return converted is INamedTypeSymbol { Name: "Expression", IsGenericType: true } named
               && named.ContainingNamespace?.ToDisplayString() == "System.Linq.Expressions";
    }

    /// <summary>Whether any lambda around <paramref name="operation"/> is an expression tree.</summary>
    /// <param name="operation">The operation.</param>
    public static bool IsInExpressionTree(IOperation operation)
    {
        for (var current = operation.Parent; current is not null; current = current.Parent)
        {
            if (current is IAnonymousFunctionOperation lambda && IsExpressionTree(lambda))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The root of the operation tree <paramref name="operation"/> belongs to (a method body, a field initializer).</summary>
    /// <param name="operation">The operation.</param>
    public static IOperation Root(IOperation operation)
    {
        var current = operation;
        while (current.Parent is not null)
        {
            current = current.Parent;
        }

        return current;
    }

    /// <summary>Whether <paramref name="ancestor"/> is <paramref name="operation"/> or one of its parents.</summary>
    /// <param name="operation">The inner operation.</param>
    /// <param name="ancestor">The candidate ancestor.</param>
    public static bool IsWithin(IOperation operation, IOperation ancestor)
    {
        for (var current = operation; current is not null; current = current.Parent)
        {
            if (current == ancestor)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The location to report for a call: from the method name to the end of the argument list,
    /// so the squiggle sits under <c>ToListAsync()</c> rather than the whole chain.
    /// </summary>
    /// <param name="operation">The invocation or any other operation.</param>
    public static Location ReportLocation(IOperation operation)
    {
        var syntax = operation.Syntax;
        if (syntax is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } invocation)
        {
            return Location.Create(syntax.SyntaxTree, TextSpan.FromBounds(access.Name.SpanStart, invocation.Span.End));
        }

        if (syntax is InvocationExpressionSyntax { Expression: MemberBindingExpressionSyntax binding } conditional)
        {
            return Location.Create(syntax.SyntaxTree, TextSpan.FromBounds(binding.Name.SpanStart, conditional.Span.End));
        }

        return syntax.GetLocation();
    }

    /// <summary>A short, single-line rendering of an expression for messages.</summary>
    /// <param name="operation">The operation.</param>
    public static string Describe(IOperation operation)
    {
        var text = operation.Syntax.ToString().Replace('\n', ' ').Replace('\r', ' ');
        while (text.Contains("  "))
        {
            text = text.Replace("  ", " ");
        }

        return text.Length <= 48 ? text : text.Substring(0, 45) + "...";
    }
}
