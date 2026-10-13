using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace NPlusOne.Analyzers.Analysis;

/// <summary>
/// Members of an entity that run without being named where they run: <c>ToString</c> when an entity is formatted
/// (interpolation, <c>string.Join</c>, <c>WriteLine</c>, a record's generated printer), <c>Equals</c> and
/// <c>GetHashCode</c> when entities are hashed (<c>Distinct</c>, sets), user-defined conversion operators, and a lazy
/// parent chain walked in a loop. What they read on the entity loads once per element.
/// </summary>
internal sealed partial class BlockAnalyzer
{
    /// <summary>The LINQ operators that hash or compare their elements with <c>GetHashCode</c> and <c>Equals</c>.</summary>
    private static readonly HashSet<string> HashingOperators = ["Distinct", "ToHashSet", "Union", "Intersect", "Except"];

    /// <summary>An entity in an interpolated string is formatted with its <c>ToString</c>.</summary>
    /// <param name="context">The operation context.</param>
    public void OnInterpolation(OperationAnalysisContext context)
    {
        var interpolation = (IInterpolationOperation)context.Operation;
        if (!Operations.IsInExpressionTree(interpolation))
        {
            CheckFormatted(interpolation, interpolation.Expression);
        }
    }

    /// <summary>A user-defined conversion operator runs with the converted entity as its argument (<c>DeskLabel label = desk;</c>).</summary>
    /// <param name="context">The operation context.</param>
    public void OnConversion(OperationAnalysisContext context)
    {
        var conversion = (IConversionOperation)context.Operation;
        if (conversion.OperatorMethod is { Parameters.Length: 1 } method && !Operations.IsInExpressionTree(conversion))
        {
            CheckElementArgument(conversion, conversion.Operand, method, 0);
        }
    }

    /// <summary>A <c>HashSet&lt;T&gt;</c> built from entities hashes and compares every one of them.</summary>
    /// <param name="context">The operation context.</param>
    public void OnObjectCreation(OperationAnalysisContext context)
    {
        var creation = (IObjectCreationOperation)context.Operation;
        if (creation.Type is INamedTypeSymbol { Name: "HashSet", IsGenericType: true } set
            && set.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic"
            && creation.Arguments.Length > 0
            && !Operations.IsInExpressionTree(creation))
        {
            CheckHashedSequence(creation, creation.Arguments[0].Value);
        }
    }

    /// <summary>
    /// Calls that run entity members the code does not name: LINQ's hashing operators over a sequence of entities, and
    /// formatting calls (<c>string.Join</c>, <c>string.Concat</c>, <c>string.Format</c>, <c>Console</c> and
    /// <c>TextWriter</c> <c>Write</c>/<c>WriteLine</c>, <c>StringBuilder.Append</c>) given entities.
    /// </summary>
    /// <param name="invocation">The call.</param>
    private void CheckImplicitMembers(IInvocationOperation invocation)
    {
        var method = invocation.TargetMethod;
        if (method.ContainingType is not { } type)
        {
            return;
        }

        if (HashingOperators.Contains(method.Name) && Model.Symbols.FullName(type) == "System.Linq.Enumerable")
        {
            foreach (var argument in invocation.Arguments)
            {
                if (session.Symbols.ElementType(argument.Value.Type) is not null)
                {
                    CheckHashedSequence(invocation, argument.Value);
                }
            }

            return;
        }

        if (!IsFormatting(method, type))
        {
            return;
        }

        foreach (var argument in invocation.Arguments)
        {
            var values = argument.Value is IArrayCreationOperation { IsImplicit: true, Initializer: { } initializer } ? initializer.ElementValues : ImmutableArray.Create(argument.Value);
            foreach (var value in values)
            {
                var stripped = Operations.StripConversions(value);
                if (stripped is null || stripped.Type?.SpecialType == SpecialType.System_String)
                {
                    continue;
                }

                if (session.Symbols.ElementType(stripped.Type) is not null && method.Name is "Join" or "Concat")
                {
                    CheckFormattedSequence(invocation, stripped);
                }
                else if (value is IConversionOperation { Type.SpecialType: SpecialType.System_Object })
                {
                    CheckFormatted(invocation, stripped);
                }
            }
        }
    }

    /// <summary>Whether a method formats the objects it is given with their <c>ToString</c>.</summary>
    /// <param name="method">The method.</param>
    /// <param name="type">Its containing type.</param>
    private static bool IsFormatting(IMethodSymbol method, INamedTypeSymbol type)
        => Model.Symbols.FullName(type) switch
        {
            "System.String" => method.Name is "Join" or "Concat" or "Format",
            "System.Console" => method.Name is "Write" or "WriteLine",
            "System.Text.StringBuilder" => method.Name is "Append" or "AppendFormat",
            _ => method.Name is "Write" or "WriteLine" && InheritsFrom(type, "System.IO.TextWriter"),
        };

    /// <summary>Whether a type is or derives from the type with that full name.</summary>
    /// <param name="type">The type.</param>
    /// <param name="fullName">The base type's full name.</param>
    private static bool InheritsFrom(ITypeSymbol type, string fullName)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (Model.Symbols.FullName(current) == fullName)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>An entity formatted with its <c>ToString</c>, as one element of an iteration.</summary>
    /// <param name="operation">The formatting operation, where to report.</param>
    /// <param name="value">The formatted value.</param>
    private void CheckFormatted(IOperation operation, IOperation value)
    {
        if (Operations.StripConversions(value) is { Type: { } type } stripped && session.Entities.IsEntity(type)
            && FindMember(type, "ToString", 0) is { } toString)
        {
            CheckInstanceMember(operation, stripped, toString, operation.Syntax.GetLocation());
        }
    }

    /// <summary>Every entity of a sequence formatted with its <c>ToString</c> (<c>string.Join(", ", desks)</c>).</summary>
    /// <param name="operation">The formatting call.</param>
    /// <param name="sequence">The sequence.</param>
    private void CheckFormattedSequence(IOperation operation, IOperation sequence)
    {
        if (session.Symbols.ElementType(sequence.Type) is { } entity && FindMember(entity, "ToString", 0) is { } toString)
        {
            CheckSequenceMembers(operation, sequence, [toString], $"'{Operations.Describe(sequence)}', whose elements it formats");
        }
    }

    /// <summary>Every entity of a sequence hashed and compared with its own <c>GetHashCode</c> and <c>Equals</c>.</summary>
    /// <param name="operation">The hashing call or set creation.</param>
    /// <param name="sequence">The sequence.</param>
    private void CheckHashedSequence(IOperation operation, IOperation sequence)
    {
        if (session.Symbols.ElementType(Operations.StripConversions(sequence)?.Type) is not { } entity)
        {
            return;
        }

        // A record's generated Equals and GetHashCode compare fields, not the navigation getters: they have no summary.
        var members = new List<IMethodSymbol>();
        if (FindMember(entity, "GetHashCode", 0) is { } hash)
        {
            members.Add(hash);
        }

        if (FindMember(entity, "Equals", 1) is { } equals)
        {
            members.Add(equals);
        }

        CheckSequenceMembers(operation, Operations.StripConversions(sequence)!, members, $"'{Operations.Describe(sequence)}', whose elements it hashes and compares");
    }

    /// <summary>
    /// The navigations members read on every entity of a sequence that a call walks once, outside any loop: lazy loads
    /// once per element.
    /// </summary>
    /// <param name="operation">The call, where to report.</param>
    /// <param name="sequence">The sequence of entities.</param>
    /// <param name="members">The members run on each entity.</param>
    /// <param name="description">How messages name what walks the elements.</param>
    private void CheckSequenceMembers(IOperation operation, IOperation sequence, List<IMethodSymbol> members, string description)
    {
        if (members.Count == 0 || !session.Entities.AnyLazyLoading || session.Symbols.ElementType(sequence.Type) is not { } entity
            || !session.Entities.IsEntity(entity) || LazyLoadingTurnedOffBefore(operation))
        {
            return;
        }

        var source = _tracer.Trace(sequence).Element();
        var iteration = new Iteration(IterationKind.Callback, operation, description, false);
        foreach (var member in members)
        {
            foreach (var path in ReadsOnThis(member, entity))
            {
                if (LoadsLazily(source, entity, path))
                {
                    var full = SourceTracer.Join(source.Path, path);
                    AddLazyLoad((operation, member, full), operation.Syntax.GetLocation(), source.Root, Messages.LazyLoadPerElement(full, iteration, Model.Symbols.Display(member)));
                }
            }
        }
    }

    /// <summary>
    /// The navigation paths a member reads on its own instance: from its summary, or, for the <c>ToString</c> the
    /// compiler writes for a record, every navigation (its printer reads every public property).
    /// </summary>
    /// <param name="member">The member.</param>
    /// <param name="entity">The entity type it runs on.</param>
    private IEnumerable<string> ReadsOnThis(IMethodSymbol member, ITypeSymbol entity)
    {
        if (member is { IsImplicitlyDeclared: true, Name: "ToString", ContainingType.IsRecord: true })
        {
            foreach (var navigation in session.Entities.NavigationsOf(entity))
            {
                if (navigation.Property.DeclaredAccessibility == Accessibility.Public)
                {
                    yield return navigation.Property.Name;
                }
            }

            yield break;
        }

        foreach (var read in session.NavigationReadsOf(member, ParameterNavigationRead.This))
        {
            if (!read.PerElement)
            {
                yield return read.Path;
            }
        }
    }

    /// <summary>
    /// The instance method of that name and arity a type or its base types (short of <c>object</c>) declare: an override
    /// of <c>ToString</c>, <c>Equals</c> or <c>GetHashCode</c>, or the one the compiler writes for a record.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <param name="name">The method name.</param>
    /// <param name="parameters">Its parameter count.</param>
    private static IMethodSymbol? FindMember(ITypeSymbol type, string name, int parameters)
    {
        for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            foreach (var member in current.GetMembers(name))
            {
                if (member is IMethodSymbol { IsStatic: false } method && method.Parameters.Length == parameters
                    && (parameters == 0 || method.Parameters[0].Type.SpecialType == SpecialType.System_Object))
                {
                    return method;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// An element of an iteration passed to a method that reads its navigations (a mapper, a conversion operator) lazy-loads
    /// them once per element.
    /// </summary>
    /// <param name="operation">The call or conversion, where to report.</param>
    /// <param name="value">The value passed.</param>
    /// <param name="method">The method it is passed to.</param>
    /// <param name="ordinal">The parameter it is passed as.</param>
    private void CheckElementArgument(IOperation operation, IOperation value, IMethodSymbol method, int ordinal)
    {
        if (_lazy.ElementOf(value) is not { } element || value.Type is not { } elementType || LazyLoadingTurnedOffBefore(operation))
        {
            return;
        }

        var source = _tracer.Trace(value);
        var reads = operation is IInvocationOperation call ? session.NavigationReadsOf(call, ordinal) : session.NavigationReadsOf(method, ordinal);
        foreach (var read in reads)
        {
            if (!read.PerElement && LoadsLazily(source, elementType, read.Path))
            {
                var full = SourceTracer.Join(source.Path, read.Path);
                AddLazyLoad((element.Iteration.Region, element.Root, full), Operations.ReportLocation(operation), source.Root, Messages.LazyLoadPerElement(full, element.Iteration, Model.Symbols.Display(method)));
            }
        }
    }

    /// <summary>
    /// Where a walk up a lazy chain starts: what the local holds before the loop, its assignments other than the steps
    /// from its own navigation (each step loads from the entity before it, which came from the same query).
    /// </summary>
    /// <param name="local">The local that walks the chain.</param>
    private SourceInfo WalkStart(ILocalReferenceOperation local)
    {
        var start = SourceInfo.Pending;
        foreach (var value in Operations.AssignedValues(local.Local, local))
        {
            if (Operations.StripConversions(value) is not IPropertyReferenceOperation { Instance: ILocalReferenceOperation from }
                || !SymbolEqualityComparer.Default.Equals(from.Local, local.Local))
            {
                start = start.Combine(_tracer.Trace(value));
            }
        }

        return start.Kind == OriginKind.Pending ? SourceInfo.Unknown : start;
    }

    /// <summary>
    /// A loop that walks a lazy chain (<c>while (floor.Parent != null) floor = floor.Parent;</c>): the local is reassigned
    /// from its own navigation in the loop, so every step loads one more row. Null when the read is not such a walk.
    /// </summary>
    /// <param name="property">A lazy navigation read.</param>
    private static Iteration? ChainWalk(IPropertyReferenceOperation property)
    {
        if (Operations.StripConversions(property.Instance) is not ILocalReferenceOperation local)
        {
            return null;
        }

        for (var current = property.Parent; current is not null; current = current.Parent)
        {
            if (current is not ILoopOperation loop)
            {
                continue;
            }

            foreach (var operation in loop.Body.DescendantsAndSelf())
            {
                if (operation is ISimpleAssignmentOperation { Target: ILocalReferenceOperation target } assignment
                    && SymbolEqualityComparer.Default.Equals(target.Local, local.Local)
                    && Operations.StripConversions(assignment.Value) is IPropertyReferenceOperation { Instance: ILocalReferenceOperation from } step
                    && SymbolEqualityComparer.Default.Equals(from.Local, local.Local)
                    && SymbolEqualityComparer.Default.Equals(step.Property.OriginalDefinition, property.Property.OriginalDefinition))
                {
                    return new Iteration(IterationKind.Loop, loop, $"the loop that walks '{local.Local.Name}' up its '{property.Property.Name}' chain", false);
                }
            }

            return null;
        }

        return null;
    }
}
