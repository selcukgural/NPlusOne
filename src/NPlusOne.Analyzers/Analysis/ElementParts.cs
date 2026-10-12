using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using NPlusOne.Analyzers.Model;

namespace NPlusOne.Analyzers.Analysis;

/// <summary>
/// What one part of an element is built from: the expression put into a tuple per element (<c>o</c> in
/// <c>orders.Select(o =&gt; (o.Id, o))</c>), or the element of a sequence itself (the value of
/// <c>orders.ToDictionary(o =&gt; o.Id)</c>, the item of <c>orders.Index()</c>). Exactly one of the two is set.
/// </summary>
/// <param name="Value">The expression the part was built from, inside the lambda that builds each element.</param>
/// <param name="ElementOf">The sequence whose element the part is.</param>
internal sealed record ElementPart(IOperation? Value, IOperation? ElementOf);

/// <summary>
/// Follows the parts of tuple, key/value pair and record elements (<c>foreach (var (id, order) in pairs)</c>,
/// <c>pair.Order</c>, <c>pair.Value</c>, <c>row.Order</c>) back to what each element was built from, so a part is an
/// element of an iteration only when it really differs per element: an entity put into every tuple is the same entity
/// each time. Only the shapes whose parts can be read off the code are followed: a tuple, a key/value pair or an object
/// built in a <c>Select</c> lambda, <c>ToDictionary</c> (with a key selector, or over such tuples), <c>Index</c> and
/// <c>Zip</c>; any other part is unknown.
/// </summary>
internal static class ElementParts
{
    /// <summary>How many locals and pass-through calls are followed back to the call that builds the elements.</summary>
    private const int MaxHops = 4;

    /// <summary>
    /// The element a part reference reads, and which part: <c>pair.Order</c> on a tuple gives the tuple's underlying
    /// field name (<c>Item2</c>), <c>pair.Value</c> on a <c>KeyValuePair</c> gives <c>Value</c>, <c>row.Order</c> on a
    /// record or a plain auto-property of a class gives <c>Order</c>; null for anything else. A navigation is never asked
    /// about: callers handle navigations first.
    /// </summary>
    /// <param name="operation">The reference, conversions stripped.</param>
    public static (IOperation Instance, string Member)? MemberOf(IOperation? operation)
    {
        if (operation is IFieldReferenceOperation { Instance: { } tuple, Field: { ContainingType.IsTupleType: true } field })
        {
            return (tuple, (field.CorrespondingTupleField ?? field).Name);
        }

        if (operation is IPropertyReferenceOperation { Instance: { } pair, Property: { Name: "Value", ContainingType: { } type } } && IsKeyValuePair(type))
        {
            return (pair, "Value");
        }

        if (operation is IPropertyReferenceOperation { Instance: { } holder, Property: { IsIndexer: false, IsStatic: false } property }
            && (property.ContainingType.IsRecord || Session.IsAutoProperty(property)))
        {
            return (holder, property.Name);
        }

        return null;
    }

    /// <summary>
    /// Which part of the element a local deconstructed by a <c>foreach</c> holds: <c>Item1</c>, <c>Item2</c>, ... of a
    /// tuple, <c>Key</c> or <c>Value</c> of a key/value pair; null when the loop does not deconstruct into it, or
    /// deconstructs a type of another kind (its <c>Deconstruct</c> method decides).
    /// </summary>
    /// <param name="loop">The loop.</param>
    /// <param name="local">One of the loop's locals.</param>
    /// <param name="symbols">The known symbols, for the element type.</param>
    public static string? DeconstructedMember(IForEachLoopOperation loop, ILocalSymbol local, KnownSymbols symbols)
    {
        if (StripDeclaration(loop.LoopControlVariable) is not ITupleOperation tuple)
        {
            return null;
        }

        for (var i = 0; i < tuple.Elements.Length; i++)
        {
            if (StripDeclaration(tuple.Elements[i]) is not ILocalReferenceOperation reference || !SymbolEqualityComparer.Default.Equals(reference.Local, local))
            {
                continue;
            }

            return symbols.ElementType(Operations.StripConversions(loop.Collection)?.Type) switch
            {
                { IsTupleType: true } => "Item" + (i + 1),
                { } element when IsKeyValuePair(element) => i == 0 ? "Key" : "Value",
                { } element => DeconstructedName(element, i, tuple.Elements.Length),
                _ => null,
            };
        }

        return null;
    }

    /// <summary>
    /// The name of the part a type's <c>Deconstruct</c> puts at a position: its out parameter's name, which for a
    /// positional record is the property's (<c>Deconstruct(out int Id, out Order Order)</c>).
    /// </summary>
    /// <param name="type">The deconstructed element type.</param>
    /// <param name="position">The position.</param>
    /// <param name="count">How many parts the loop takes.</param>
    private static string? DeconstructedName(ITypeSymbol type, int position, int count)
    {
        foreach (var member in type.GetMembers("Deconstruct"))
        {
            if (member is IMethodSymbol { IsStatic: false } deconstruct && deconstruct.Parameters.Length == count)
            {
                return deconstruct.Parameters[position].Name;
            }
        }

        return null;
    }

    /// <summary>Whether an operation declares the iteration variables of a <c>foreach</c> (its loop control variable or a part of it).</summary>
    /// <param name="operation">The operation.</param>
    public static bool IsLoopControlVariable(IOperation operation)
    {
        var current = operation;
        while (current.Parent is ITupleOperation or IDeclarationExpressionOperation)
        {
            current = current.Parent;
        }

        return current.Parent is IForEachLoopOperation loop && loop.LoopControlVariable == current;
    }

    /// <summary>The <c>foreach</c> that declares a local as its plain iteration variable (not deconstructed), or null.</summary>
    /// <param name="local">The local.</param>
    /// <param name="anyOperation">Any operation of the method the local belongs to.</param>
    public static IForEachLoopOperation? LoopDeclaring(ILocalSymbol local, IOperation anyOperation)
    {
        foreach (var operation in Operations.Root(anyOperation).Descendants())
        {
            if (operation is IForEachLoopOperation { LoopControlVariable: IVariableDeclaratorOperation declarator } loop
                && SymbolEqualityComparer.Default.Equals(declarator.Symbol, local))
            {
                return loop;
            }
        }

        return null;
    }

    /// <summary>
    /// What a part of the elements of <paramref name="collection"/> is built from, following locals and calls that
    /// only copy the sequence (<c>ToList</c>, <c>ToArray</c>, <c>AsEnumerable</c>) back to the call that builds it.
    /// </summary>
    /// <param name="collection">The sequence whose elements are split.</param>
    /// <param name="member">The part: <c>ItemN</c> or <c>Value</c>.</param>
    public static ElementPart? Resolve(IOperation collection, string member)
    {
        if (Producer(collection) is not { } producer)
        {
            return null;
        }

        var source = producer.Instance ?? (producer.Arguments.Length > 0 ? producer.Arguments[0].Value : null);
        switch (producer.TargetMethod.Name)
        {
            case "Select" when member.StartsWith("Item", StringComparison.Ordinal) && int.TryParse(member.Substring(4), out var position)
                               && Operations.StripConversions(LambdaArgument(producer, "selector")) is ITupleOperation tuple:
                return position <= tuple.Elements.Length ? new ElementPart(tuple.Elements[position - 1], null) : null;
            case "Select":
                return Operations.StripConversions(LambdaArgument(producer, "selector")) is IObjectCreationOperation creation
                       && CreatedMember(creation, member) is { } created
                    ? new ElementPart(created, null)
                    : null;
            case "Zip" when member.StartsWith("Item", StringComparison.Ordinal) && int.TryParse(member.Substring(4), out var zipped)
                            && !HasArgument(producer, "resultSelector"):
                // Zip pairs the n-th elements of its sequences: Item1 is an element of the first, Item2 of the second.
                return SequenceAt(producer, zipped - 1) is { } sequence ? new ElementPart(null, sequence) : null;
            case "ToDictionary" or "ToDictionaryAsync" when member == "Value":
                if (LambdaArgument(producer, "elementSelector") is { } value)
                {
                    return new ElementPart(value, null);
                }

                if (source is null || HasArgument(producer, "elementSelector"))
                {
                    return null;
                }

                // ToDictionary() over (key, value) tuples: each value is the second part of a source element.
                if (!HasArgument(producer, "keySelector"))
                {
                    return SourceElementsAreTuples(producer) ? Resolve(source, "Item2") : null;
                }

                return new ElementPart(null, source);
            case "Index" when member == "Item2":
                return source is null ? null : new ElementPart(null, source);
            default:
                return null;
        }
    }

    /// <summary>
    /// What an object created per element puts into a member: a <c>KeyValuePair</c>'s key and value, the constructor
    /// argument whose parameter has the member's name (case aside, as a record's positional parameters do), or the value
    /// an object initializer assigns to it.
    /// </summary>
    /// <param name="creation">The object creation in the selector.</param>
    /// <param name="member">The part's name.</param>
    private static IOperation? CreatedMember(IObjectCreationOperation creation, string member)
    {
        if (creation.Type is { } type && IsKeyValuePair(type))
        {
            var index = member switch
            {
                "Key" => 0,
                "Value" => 1,
                _ => -1,
            };
            return index >= 0 && index < creation.Arguments.Length ? creation.Arguments[index].Value : null;
        }

        foreach (var argument in creation.Arguments)
        {
            if (string.Equals(argument.Parameter?.Name, member, StringComparison.OrdinalIgnoreCase))
            {
                return argument.Value;
            }
        }

        foreach (var initializer in creation.Initializer?.Initializers ?? ImmutableArray<IOperation>.Empty)
        {
            if (initializer is ISimpleAssignmentOperation { Target: IPropertyReferenceOperation target } assignment && target.Property.Name == member)
            {
                return assignment.Value;
            }
        }

        return null;
    }

    /// <summary>The sequence a LINQ call takes at a position: its receiver or first argument for 0, the next arguments after it.</summary>
    /// <param name="call">The call.</param>
    /// <param name="position">The position.</param>
    private static IOperation? SequenceAt(IInvocationOperation call, int position)
    {
        var sequences = new List<IOperation>();
        if (call.Instance is { } instance)
        {
            sequences.Add(instance);
        }

        foreach (var argument in call.Arguments)
        {
            if (Operations.AsLambda(argument.Value) is null)
            {
                sequences.Add(argument.Value);
            }
        }

        return position >= 0 && position < sequences.Count ? sequences[position] : null;
    }

    /// <summary>The call that builds a sequence's elements, through locals assigned once and copying calls.</summary>
    /// <param name="collection">The sequence.</param>
    private static IInvocationOperation? Producer(IOperation collection)
    {
        var current = collection;
        for (var hops = 0; hops <= MaxHops; hops++)
        {
            switch (Operations.StripConversions(current))
            {
                case IAwaitOperation awaited:
                    current = awaited.Operation;
                    break;
                case ILocalReferenceOperation local when Operations.SingleInitializer(local) is { } initializer:
                    current = initializer;
                    break;
                case IInvocationOperation { TargetMethod.Name: "ToList" or "ToArray" or "ToListAsync" or "ToArrayAsync" or "AsEnumerable" } copy:
                    current = copy.Instance ?? (copy.Arguments.Length > 0 ? copy.Arguments[0].Value : null);
                    break;
                case IInvocationOperation call when IsLinq(call.TargetMethod):
                    return call;
                default:
                    return null;
            }

            if (current is null)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>What a lambda passed for a parameter returns, or null when no lambda with one result is passed.</summary>
    /// <param name="call">The call.</param>
    /// <param name="parameter">The parameter name.</param>
    private static IOperation? LambdaArgument(IInvocationOperation call, string parameter)
    {
        foreach (var argument in call.Arguments)
        {
            if (argument.Parameter?.Name == parameter && Operations.AsLambda(argument.Value) is { } lambda)
            {
                return Operations.LambdaResult(lambda);
            }
        }

        return null;
    }

    /// <summary>Whether the sequence a LINQ call takes first holds tuples (<c>IEnumerable&lt;(TKey Key, TValue Value)&gt;</c>).</summary>
    /// <param name="call">The call.</param>
    private static bool SourceElementsAreTuples(IInvocationOperation call)
        => call.TargetMethod.Parameters.Length > 0
           && call.TargetMethod.Parameters[0].Type is INamedTypeSymbol { TypeArguments.Length: 1 } sequence
           && sequence.TypeArguments[0].IsTupleType;

    /// <summary>Whether a call passes an argument for a parameter.</summary>
    /// <param name="call">The call.</param>
    /// <param name="parameter">The parameter name.</param>
    private static bool HasArgument(IInvocationOperation call, string parameter)
    {
        foreach (var argument in call.Arguments)
        {
            if (argument.Parameter?.Name == parameter)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a method is a LINQ or EF Core sequence operator, whose meaning is known by its name.</summary>
    /// <param name="method">The method.</param>
    private static bool IsLinq(IMethodSymbol method)
        => method.ContainingType is { } type
           && Symbols.FullName(type) is "System.Linq.Enumerable" or "System.Linq.Queryable" or "System.Linq.AsyncEnumerable"
               or "Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions";

    /// <summary>Whether a type is <c>System.Collections.Generic.KeyValuePair&lt;TKey, TValue&gt;</c>.</summary>
    /// <param name="type">The type.</param>
    private static bool IsKeyValuePair(ITypeSymbol type)
        => type is INamedTypeSymbol { Name: "KeyValuePair", Arity: 2 } named && Symbols.FullName(named) == "System.Collections.Generic.KeyValuePair";

    /// <summary>Removes the <c>var</c> declaration around a deconstruction target.</summary>
    /// <param name="operation">The operation.</param>
    private static IOperation? StripDeclaration(IOperation? operation)
        => operation is IDeclarationExpressionOperation declaration ? declaration.Expression : operation;
}
