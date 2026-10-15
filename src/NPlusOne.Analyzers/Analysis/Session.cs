using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using NPlusOne.Analyzers.Model;

namespace NPlusOne.Analyzers.Analysis;

/// <summary>
/// The analysis state of one compilation: its symbols, settings and entity model, the method
/// summaries computed so far, and the transitive questions answered from them (does this call make a
/// round trip, does this method call its delegate per element). Thread-safe; analyzer callbacks run
/// concurrently.
/// </summary>
internal sealed class Session
{
    /// <summary>The most implementations of one interface or virtual method that are followed.</summary>
    private const int MaxImplementations = 16;

    /// <summary>The most summaries built inside one another on a thread.</summary>
    private const int MaxBuildDepth = 64;

    /// <summary>The handler and pipeline behavior interfaces of MediatR and the Mediator library, without arity.</summary>
    private static readonly ImmutableHashSet<string> MediatorInterfaces = ImmutableHashSet.Create(
        StringComparer.Ordinal, "IRequestHandler", "ICommandHandler", "IQueryHandler", "IStreamRequestHandler", "INotificationHandler", "IPipelineBehavior");

    /// <summary>Methods whose summary is being built on this thread, so recursion yields an empty summary instead of looping.</summary>
    [ThreadStatic]
    private static HashSet<IMethodSymbol>? _building;

    /// <summary>Fields and properties whose assigned source is being collected on this thread, so a cycle stops.</summary>
    [ThreadStatic]
    private static HashSet<ISymbol>? _collecting;

    private readonly ConcurrentDictionary<IMethodSymbol, MethodSummary> _summaries = new(SymbolEqualityComparer.Default);
    private readonly ConcurrentDictionary<IMethodSymbol, ImmutableArray<IMethodSymbol>> _targets = new(SymbolEqualityComparer.Default);
    private readonly ConcurrentDictionary<WitnessKey, Witness?> _witnesses = new();
    private readonly Lazy<ImmutableArray<INamedTypeSymbol>> _sourceTypes;
    private readonly Lazy<ImmutableArray<INamedTypeSymbol>> _completeTypes;
    private readonly Lazy<ImmutableArray<INamedTypeSymbol>> _implementing;
    private readonly Lazy<ImmutableArray<INamedTypeSymbol>> _overriding;
    private readonly Func<ImmutableArray<INamedTypeSymbol>> _implementingCandidates;
    private readonly Func<IMethodSymbol, ImmutableArray<IMethodSymbol>> _findTargets;
    private readonly Func<ISymbol, ImmutableArray<IOperation>> _collectAssignedValues;
    private readonly DeclarationFacts _facts;
    private readonly ConcurrentDictionary<string, Lazy<ImmutableArray<INamedTypeSymbol>>> _mediatorTypes = new(StringComparer.Ordinal);
    private readonly Lazy<ReferencedSummaries> _referenced;
    private readonly Lazy<ImmutableHashSet<IMethodSymbol>> _endpointHandlers;
    private readonly ConcurrentDictionary<ISymbol, SourceInfo> _assigned = new(SymbolEqualityComparer.Default);
    private readonly ConcurrentDictionary<ISymbol, ImmutableArray<IOperation>> _assignedValues = new(SymbolEqualityComparer.Default);
    private readonly ConcurrentDictionary<(ITypeSymbol Request, string Call), ImmutableArray<IMethodSymbol>> _handlers = new();
    private readonly Lazy<ImmutableHashSet<ITypeSymbol>> _registeredBehaviors;
    private readonly Lazy<bool> _mvcUsesNewtonsoft;

    /// <summary>Creates the state for a compilation.</summary>
    /// <param name="compilation">The compilation.</param>
    /// <param name="symbols">Its known symbols.</param>
    /// <param name="settings">The analyzer options.</param>
    public Session(Compilation compilation, KnownSymbols symbols, Settings settings)
    {
        Compilation = compilation;
        Symbols = symbols;
        Settings = settings;
        _referenced = new Lazy<ReferencedSummaries>(() => ReferencedSummaries.Read(compilation));
        _facts = DeclarationFacts.For(compilation);
        Entities = new EntityModel(compilation, symbols, settings.LazyLoading, () => _referenced.Value.AutoIncludes(compilation), _facts);
        _sourceTypes = new Lazy<ImmutableArray<INamedTypeSymbol>>(CollectSourceTypes);
        _completeTypes = new Lazy<ImmutableArray<INamedTypeSymbol>>(CollectCompleteTypes);
        _implementing = new Lazy<ImmutableArray<INamedTypeSymbol>>(() => CollectCandidateTypes(Model.Symbols.MayDeriveOrImplement));
        _overriding = new Lazy<ImmutableArray<INamedTypeSymbol>>(() => CollectCandidateTypes(Model.Symbols.MayOverride));
        _implementingCandidates = () => _implementing.Value;

        // Method groups kept as delegates, so a cache lookup that finds its value allocates nothing.
        _findTargets = FindTargets;
        _collectAssignedValues = CollectAssignedValues;
        Mappings = new Mappings(compilation, Entities, symbols, _facts);
        _endpointHandlers = new Lazy<ImmutableHashSet<IMethodSymbol>>(CollectEndpointHandlers);
        _registeredBehaviors = new Lazy<ImmutableHashSet<ITypeSymbol>>(CollectRegisteredBehaviors);
        _mvcUsesNewtonsoft = new Lazy<bool>(CallsAddNewtonsoftJson);
    }

    /// <summary>
    /// Whether the compilation switches MVC to Newtonsoft.Json (<c>AddControllers().AddNewtonsoftJson()</c>): its
    /// controllers' results are then written by Newtonsoft, which honours Newtonsoft's ignore attributes. Minimal APIs
    /// and SignalR keep System.Text.Json.
    /// </summary>
    public bool MvcUsesNewtonsoft => _mvcUsesNewtonsoft.Value;

    /// <summary>Whether a file of the compilation calls <c>AddNewtonsoftJson</c> on an MVC builder.</summary>
    private bool CallsAddNewtonsoftJson()
    {
        foreach (var tree in Compilation.SyntaxTrees)
        {
            if (!SyntaxIndex.HasCallsTo(tree, "AddNewtonsoftJson"))
            {
                continue;
            }

            SemanticModel? model = null;
            foreach (var call in SyntaxIndex.CallsTo(tree, "AddNewtonsoftJson"))
            {
#pragma warning disable RS1030 // The registration lives in the startup code, not in the controllers; this runs once per compilation.
                model ??= Compilation.GetSemanticModel(tree);
#pragma warning restore RS1030
                if (model.GetSymbolInfo(call).Symbol is IMethodSymbol { Name: "AddNewtonsoftJson", ContainingType.Name: "NewtonsoftJsonMvcBuilderExtensions" })
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>The compilation being analysed.</summary>
    public Compilation Compilation { get; }

    /// <summary>Its known symbols.</summary>
    public KnownSymbols Symbols { get; }

    /// <summary>The analyzer options.</summary>
    public Settings Settings { get; }

    /// <summary>Its entities and navigations.</summary>
    public EntityModel Entities { get; }

    /// <summary>What object mappers (AutoMapper, Mapster) read on the entities they map.</summary>
    public Mappings Mappings { get; }

    /// <summary>Every named type declared in the compilation's source, nested ones included.</summary>
    public ImmutableArray<INamedTypeSymbol> SourceTypes => _sourceTypes.Value;

    /// <summary>
    /// Whether the analysis knows what a method does: its body is in the compilation, or its assembly
    /// wrote summaries (a method of such an assembly without facts makes no round trip).
    /// </summary>
    /// <param name="method">The method.</param>
    public bool HasSummary(IMethodSymbol method) => Model.Symbols.HasSource(method, Compilation) || _referenced.Value.Covers(method);

    /// <summary>
    /// Whether <paramref name="method"/> calls the delegate passed as <paramref name="parameter"/> once per
    /// element: LINQ to Objects, <c>ForEach</c>, <c>Parallel</c>, EF Core's <c>ForEachAsync</c>, methods
    /// configured in <c>nplusone.iterating_methods</c>, and source methods whose summary shows it.
    /// </summary>
    /// <param name="method">The method a callback is passed to.</param>
    /// <param name="parameter">The parameter the callback binds to.</param>
    public bool IsIterating(IMethodSymbol method, IParameterSymbol? parameter)
    {
        if (parameter is null || parameter.Type.TypeKind != TypeKind.Delegate)
        {
            return false;
        }

        var type = method.ContainingType?.OriginalDefinition;
        if (Is(type, Symbols.Queryable))
        {
            return false;
        }

        if (Is(type, Symbols.Enumerable) || Is(type, Symbols.AsyncEnumerable))
        {
            return true;
        }

        if (Is(type, Symbols.Parallel))
        {
            return method.Name is "For" or "ForEach" or "ForEachAsync";
        }

        if (method.Name is "ForEach" or "ForEachAsync")
        {
            return true;
        }

        if (Settings.IteratingMethods.MatchesMethod(method))
        {
            return true;
        }

        return HasSummary(method) && Summary(method.OriginalDefinition).DelegatesInvokedPerElement.Contains(parameter.Ordinal);
    }

    /// <summary>
    /// The source methods a call can run: the method itself when it has a body, plus every
    /// implementation or override in the compilation when it is dispatched.
    /// </summary>
    /// <param name="method">The method called.</param>
    public ImmutableArray<IMethodSymbol> TargetsOf(IMethodSymbol method)
        => _targets.TryGetValue(method.OriginalDefinition, out var targets) ? targets : _targets.GetOrAdd(method.OriginalDefinition, _findTargets);

    /// <summary>
    /// The source methods a call can run, as <see cref="TargetsOf(IMethodSymbol)"/> when it is dispatched; a call that is
    /// not (<c>base.GetAsync(id)</c> in an override) runs only the method's own body, never what overrides it.
    /// </summary>
    /// <param name="method">The method called.</param>
    /// <param name="dispatched">Whether the call is dispatched, as <see cref="IInvocationOperation.IsVirtual"/>.</param>
    private ImmutableArray<IMethodSymbol> TargetsOf(IMethodSymbol method, bool dispatched)
    {
        if (dispatched)
        {
            return TargetsOf(method);
        }

        var definition = method.OriginalDefinition;
        return !definition.IsAbstract && HasSummary(definition) ? ImmutableArray.Create(definition) : ImmutableArray<IMethodSymbol>.Empty;
    }

    /// <summary>The summary of a source method, or an empty one when it has no body here.</summary>
    /// <param name="method">The method (its original definition).</param>
    public MethodSummary Summary(IMethodSymbol method)
    {
        if (_summaries.TryGetValue(method, out var cached))
        {
            return cached;
        }

        // A long chain of methods that each need the next one's summary builds them all on one stack: past the cap the
        // callee counts as empty (its round trips are missed, never invented) so the analyzer cannot overflow the stack.
        var building = _building ??= new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        if (building.Count >= MaxBuildDepth || !building.Add(method))
        {
            return MethodSummary.Empty;
        }

        try
        {
            var summary = BuildSummary(method);
            return _summaries.GetOrAdd(method, summary);
        }
        finally
        {
            building.Remove(method);
        }
    }

    /// <summary>What a call to <paramref name="method"/> returns, combined over every method it can run.</summary>
    /// <param name="method">The method called.</param>
    public SourceInfo ReturnsOf(IMethodSymbol method) => ReturnsOf(TargetsOf(method));

    /// <summary>What a call returns, combined over every method it can run (only the callee's own body for a base call).</summary>
    /// <param name="call">The call.</param>
    public SourceInfo ReturnsOf(IInvocationOperation call) => ReturnsOf(TargetsOf(call.TargetMethod, call.IsVirtual));

    /// <summary>What running one of the given methods returns, combined over all of them.</summary>
    /// <param name="targets">The methods a call can run.</param>
    private SourceInfo ReturnsOf(ImmutableArray<IMethodSymbol> targets)
    {
        var result = SourceInfo.Pending;
        foreach (var target in targets)
        {
            result = result.Combine(Summary(target).Returns);
        }

        return result.Kind == OriginKind.Pending ? SourceInfo.Unknown : result;
    }

    /// <summary>
    /// Proof that calling <paramref name="method"/> with these arguments makes a round trip of the
    /// given kind, found in its summary or its callees' up to the configured depth; null when none is
    /// found. Methods in <c>nplusone.ignored_methods</c> never make one, methods in
    /// <c>nplusone.query_methods</c> always make a query.
    /// </summary>
    /// <param name="method">The method called.</param>
    /// <param name="arguments">What each argument is, by parameter ordinal.</param>
    /// <param name="kind">Query or save.</param>
    public Witness? FindWitness(IMethodSymbol method, ImmutableArray<SourceInfo> arguments, EffectKind kind)
        => FindWitness(method, arguments, kind, Settings.MaxCallDepth, true);

    /// <summary>
    /// Proof that a call makes a round trip of the given kind, as <see cref="FindWitness(IMethodSymbol, ImmutableArray{SourceInfo}, EffectKind)"/>;
    /// a base call is looked for in the callee's own body only.
    /// </summary>
    /// <param name="call">The call.</param>
    /// <param name="arguments">What each argument is, by parameter ordinal.</param>
    /// <param name="kind">Query or save.</param>
    public Witness? FindWitness(IInvocationOperation call, ImmutableArray<SourceInfo> arguments, EffectKind kind)
        => FindWitness(call.TargetMethod, arguments, kind, Settings.MaxCallDepth, call.IsVirtual);

    /// <summary>
    /// The lazy navigations a method reads, directly or through the methods it passes the
    /// parameter to, on the parameter with the given ordinal (<see cref="ParameterNavigationRead.This"/>
    /// for the instance): on the parameter itself or on each of its elements.
    /// </summary>
    /// <param name="method">The method called.</param>
    /// <param name="ordinal">The parameter ordinal.</param>
    public ImmutableArray<ParameterNavigationRead> NavigationReadsOf(IMethodSymbol method, int ordinal)
        => NavigationReadsOf(TargetsOf(method), ordinal);

    /// <summary>The lazy navigations a call reads on a parameter, as <see cref="NavigationReadsOf(IMethodSymbol, int)"/>; a base call reads what the callee's own body reads.</summary>
    /// <param name="call">The call.</param>
    /// <param name="ordinal">The parameter ordinal.</param>
    public ImmutableArray<ParameterNavigationRead> NavigationReadsOf(IInvocationOperation call, int ordinal)
        => NavigationReadsOf(TargetsOf(call.TargetMethod, call.IsVirtual), ordinal);

    /// <summary>The lazy navigations the given methods read on a parameter.</summary>
    /// <param name="targets">The methods a call can run.</param>
    /// <param name="ordinal">The parameter ordinal.</param>
    private ImmutableArray<ParameterNavigationRead> NavigationReadsOf(ImmutableArray<IMethodSymbol> targets, int ordinal)
    {
        var builder = ImmutableArray.CreateBuilder<ParameterNavigationRead>();
        CollectNavigationReads(targets, ordinal, false, Settings.MaxCallDepth, new NavigationReadSearch(builder));
        return builder.ToImmutable();
    }

    /// <summary>
    /// The handlers a mediator call runs: for <c>ISender.Send(request)</c> / <c>IMediator.Send</c> the
    /// <c>Handle</c> methods of the <c>IRequestHandler&lt;TRequest, ...&gt;</c> implementations for the request's
    /// type (and the source-generated Mediator library's <c>ICommandHandler</c> and <c>IQueryHandler</c>), for
    /// <c>IPublisher.Publish(notification)</c> those of the <c>INotificationHandler&lt;T&gt;</c>
    /// implementations, for <c>CreateStream(request)</c> those of the <c>IStreamRequestHandler&lt;TRequest, T&gt;</c>
    /// implementations. MediatR and the Mediator library share these names. MediatR's <c>Send</c> also runs the
    /// <c>Handle</c> of every registered <c>IPipelineBehavior&lt;TRequest, TResponse&gt;</c> whose constraints accept the
    /// request. Empty for any other call.
    /// </summary>
    /// <param name="invocation">The call.</param>
    public ImmutableArray<IMethodSymbol> MediatorHandlers(IInvocationOperation invocation)
    {
        var method = invocation.TargetMethod;
        if (method.Name is not ("Send" or "Publish" or "CreateStream")
            || method.ContainingType is not { Name: "ISender" or "IMediator" or "IPublisher" or "Mediator" } mediator
            || mediator.ContainingNamespace?.ToDisplayString() is not ("MediatR" or "Mediator")
            || invocation.Arguments.Length == 0
            || RequestType(invocation.Arguments[0].Value) is not { } request)
        {
            return ImmutableArray<IMethodSymbol>.Empty;
        }

        var key = (request, method.Name);
        return _handlers.TryGetValue(key, out var handlers)
            ? handlers
            : _handlers.GetOrAdd(key, k => FindHandlers(k.Request, k.Call, mediator.ContainingNamespace.ToDisplayString()));
    }

    /// <summary>
    /// The type of the request a mediator call sends: the argument's own type, or, for a local typed as
    /// <c>object</c> or an interface (<c>object request = new GetX(id); sender.Send(request)</c>), the one concrete
    /// type every value assigned to it in the method has.
    /// </summary>
    /// <param name="argument">The request argument.</param>
    private static ITypeSymbol? RequestType(IOperation argument)
    {
        var value = Operations.StripConversions(argument);
        if (value is not ILocalReferenceOperation { Local: var local, Type: { TypeKind: TypeKind.Interface } or { SpecialType: SpecialType.System_Object } })
        {
            return value?.Type;
        }

        ITypeSymbol? assigned = null;
        foreach (var operation in OperationIndex.Of(argument).WritesOf(local))
        {
            var stored = operation switch
            {
                IVariableDeclaratorOperation declarator when SymbolEqualityComparer.Default.Equals(declarator.Symbol, local) => declarator.Initializer?.Value,
                ISimpleAssignmentOperation { Target: ILocalReferenceOperation target } assignment when SymbolEqualityComparer.Default.Equals(target.Local, local) => assignment.Value,
                _ => null,
            };
            if (stored is null)
            {
                continue;
            }

            var type = Operations.StripConversions(stored)?.Type;
            if (type is null || assigned is not null && !SymbolEqualityComparer.Default.Equals(type, assigned))
            {
                return value.Type;
            }

            assigned = type;
        }

        return assigned ?? value.Type;
    }

    /// <summary>The <c>Handle</c> methods of the source types that handle a request type.</summary>
    /// <param name="request">The request or notification type.</param>
    /// <param name="call">The mediator method called: <c>Send</c> or <c>Publish</c>.</param>
    /// <param name="mediatorNamespace">The namespace of the mediator library.</param>
    private ImmutableArray<IMethodSymbol> FindHandlers(ITypeSymbol request, string call, string mediatorNamespace)
    {
        var builder = ImmutableArray.CreateBuilder<IMethodSymbol>();
        foreach (var type in MediatorTypes(mediatorNamespace))
        {
            foreach (var iface in type.AllInterfaces)
            {
                if (!HandlesCall(iface.Name, call)
                    || iface.ContainingNamespace?.ToDisplayString() != mediatorNamespace
                    || iface.TypeArguments.Length == 0
                    || !KnownSymbols.InheritsFrom(request, iface.TypeArguments[0] as INamedTypeSymbol))
                {
                    continue;
                }

                AddHandle(builder, type, iface);
            }

            if (call == "Send" && mediatorNamespace == "MediatR" && _registeredBehaviors.Value.Contains(type.OriginalDefinition))
            {
                foreach (var iface in type.AllInterfaces)
                {
                    if (iface is { Name: "IPipelineBehavior", TypeArguments.Length: 2 } && iface.ContainingNamespace?.ToDisplayString() == "MediatR" && Accepts(iface.TypeArguments[0], request))
                    {
                        AddHandle(builder, type, iface);
                    }
                }
            }
        }

        return builder.ToImmutable();
    }

    /// <summary>
    /// The candidate types implementing a handler or pipeline behavior interface of the mediator library, found once per
    /// compilation (every <c>Send</c> asks) from the interface map kept for the compilations with the same declarations.
    /// </summary>
    /// <param name="mediatorNamespace">The namespace of the mediator library.</param>
    private ImmutableArray<INamedTypeSymbol> MediatorTypes(string mediatorNamespace)
        => _mediatorTypes.GetOrAdd(
            mediatorNamespace,
            ns => new Lazy<ImmutableArray<INamedTypeSymbol>>(() => _facts.ImplementersOfAny(Compilation, _implementingCandidates, ns, MediatorInterfaces))).Value;

    /// <summary>Adds a type's implementation of the interface's <c>Handle</c>, when its source is in the compilation.</summary>
    /// <param name="builder">The handlers found so far.</param>
    /// <param name="type">The handler or behavior type.</param>
    /// <param name="iface">The interface it implements.</param>
    private void AddHandle(ImmutableArray<IMethodSymbol>.Builder builder, INamedTypeSymbol type, INamedTypeSymbol iface)
    {
        foreach (var member in iface.GetMembers("Handle"))
        {
            if (type.FindImplementationForInterfaceMember(member) is IMethodSymbol handle && Model.Symbols.HasSource(handle, Compilation))
            {
                builder.Add(handle.OriginalDefinition);
            }
        }
    }

    /// <summary>
    /// Whether a behavior's request type argument accepts a request: the request type itself or a base type, or a type
    /// parameter whose constraints the request meets (a constraint on a generic definition, such as
    /// <c>IRequest&lt;TResponse&gt;</c>, is met by implementing that definition).
    /// </summary>
    /// <param name="accepted">The behavior's request type argument.</param>
    /// <param name="request">The request type.</param>
    private static bool Accepts(ITypeSymbol accepted, ITypeSymbol request)
    {
        if (accepted is not ITypeParameterSymbol parameter)
        {
            return IsOrImplements(request, accepted);
        }

        foreach (var constraint in parameter.ConstraintTypes)
        {
            if (!IsOrImplements(request, constraint))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether a type is, derives from or implements another, comparing generic definitions.</summary>
    /// <param name="type">The type.</param>
    /// <param name="other">The base type or interface.</param>
    private static bool IsOrImplements(ITypeSymbol type, ITypeSymbol other)
    {
        var definition = other.OriginalDefinition;
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, definition))
            {
                return true;
            }
        }

        foreach (var iface in type.AllInterfaces)
        {
            if (SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, definition))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The pipeline behavior types the compilation registers: every type named in a <c>typeof(...)</c> or as a type
    /// argument of <c>AddBehavior</c>/<c>AddOpenBehavior</c> in a file that names one of those or <c>IPipelineBehavior</c>
    /// (<c>cfg.AddOpenBehavior(typeof(AuditBehavior&lt;,&gt;))</c>,
    /// <c>services.AddTransient(typeof(IPipelineBehavior&lt;,&gt;), typeof(AuditBehavior&lt;,&gt;))</c>). MediatR runs a
    /// behavior only when it is registered, so one that is never named stays out.
    /// </summary>
    private ImmutableHashSet<ITypeSymbol> CollectRegisteredBehaviors()
    {
        var registered = ImmutableHashSet.CreateBuilder<ITypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var tree in Compilation.SyntaxTrees)
        {
            var nodes = SyntaxIndex.Of(tree).BehaviorTypes;
            if (nodes.IsEmpty)
            {
                continue;
            }

#pragma warning disable RS1030 // Registrations live in other files than the Send calls; this runs once per compilation.
            var model = Compilation.GetSemanticModel(tree);
#pragma warning restore RS1030
            foreach (var node in nodes)
            {
                var named = node switch
                {
                    TypeOfExpressionSyntax typeOf => [model.GetTypeInfo(typeOf.Type).Type],
                    GenericNameSyntax { Identifier.ValueText: "AddBehavior" or "AddOpenBehavior" } generic => generic.TypeArgumentList.Arguments.Select(a => model.GetTypeInfo(a).Type),
                    _ => [],
                };
                foreach (var type in named)
                {
                    if (type is not null)
                    {
                        registered.Add(type.OriginalDefinition);
                    }
                }
            }
        }

        return registered.ToImmutable();
    }

    /// <summary>Whether a handler interface handles the mediator call: requests, commands and queries are sent, notifications published.</summary>
    /// <param name="handlerInterface">The handler interface name.</param>
    /// <param name="call">The mediator method called.</param>
    private static bool HandlesCall(string handlerInterface, string call)
        => call switch
        {
            "Send" => handlerInterface is "IRequestHandler" or "ICommandHandler" or "IQueryHandler",
            "CreateStream" => handlerInterface == "IStreamRequestHandler",
            _ => handlerInterface == "INotificationHandler",
        };

    /// <summary>
    /// What a field or auto-property holds: every value its type assigns to it (initializers,
    /// constructors, methods), combined. A query field set once in the constructor
    /// (<c>_big = db.Orders.Where(...)</c>) is EF-backed; one also assigned from a list is not known.
    /// </summary>
    /// <param name="member">The field or auto-property.</param>
    public SourceInfo AssignedSource(ISymbol member)
    {
        var definition = member.OriginalDefinition;
        if (_assigned.TryGetValue(definition, out var cached))
        {
            return cached;
        }

        // A field assigned from itself, or from another field assigned from it, stops here. The marker is per thread:
        // another thread must never read a half-collected source from the shared cache. Only the outermost source is
        // cached, since an inner one may have met the cycle and miss what the outer field holds.
        var collecting = _collecting ??= new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        if (!collecting.Add(definition))
        {
            return SourceInfo.Pending;
        }

        try
        {
            var result = CollectAssignedSource(definition);
            return collecting.Count == 1 ? _assigned.GetOrAdd(definition, result) : result;
        }
        finally
        {
            collecting.Remove(definition);
        }
    }

    /// <summary>Whether a property is an auto-property declared in this compilation (no accessor bodies).</summary>
    /// <param name="property">The property.</param>
    public static bool IsAutoProperty(IPropertySymbol property)
    {
        var references = property.DeclaringSyntaxReferences;
        if (references.Length == 0
            || references[0].GetSyntax() is not PropertyDeclarationSyntax { ExpressionBody: null, AccessorList: { } accessors })
        {
            return false;
        }

        foreach (var accessor in accessors.Accessors)
        {
            if (accessor.Body is not null || accessor.ExpressionBody is not null)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Combines the sources of the values assigned to the member.</summary>
    /// <param name="member">The field or auto-property.</param>
    private SourceInfo CollectAssignedSource(ISymbol member)
    {
        var result = SourceInfo.Pending;
        var tracer = new SourceTracer(this, null);
        foreach (var value in AssignedValues(member))
        {
            result = result.Combine(tracer.Trace(value));
        }

        return result.Kind == OriginKind.Pending ? SourceInfo.Unknown : result;
    }

    /// <summary>
    /// Every value the member's type assigns to a field or auto-property: initializers, assignments and <c>??=</c> in
    /// its constructors, methods and accessors. Cached per member.
    /// </summary>
    /// <param name="member">The field or auto-property.</param>
    public ImmutableArray<IOperation> AssignedValues(ISymbol member)
        => _assignedValues.TryGetValue(member.OriginalDefinition, out var values) ? values : _assignedValues.GetOrAdd(member.OriginalDefinition, _collectAssignedValues);

    /// <summary>Scans the member's type for the values assigned to it.</summary>
    /// <param name="member">The field or auto-property.</param>
    private ImmutableArray<IOperation> CollectAssignedValues(ISymbol member)
    {
        var values = ImmutableArray.CreateBuilder<IOperation>();
        if (member.ContainingType is not { } type)
        {
            return values.ToImmutable();
        }

        foreach (var reference in type.DeclaringSyntaxReferences)
        {
            if (!Compilation.ContainsSyntaxTree(reference.SyntaxTree))
            {
                continue;
            }

#pragma warning disable RS1030 // The assignments live in other members of the type; the result is cached per member.
            var model = Compilation.GetSemanticModel(reference.SyntaxTree);
#pragma warning restore RS1030
            foreach (var node in reference.GetSyntax().DescendantNodes())
            {
                if (node is not (EqualsValueClauseSyntax or ArrowExpressionClauseSyntax or BlockSyntax { Parent: BaseMethodDeclarationSyntax or AccessorDeclarationSyntax })
                    || model.GetOperation(node) is not { } operation)
                {
                    continue;
                }

                foreach (var candidate in operation.DescendantsAndSelf())
                {
                    var value = candidate switch
                    {
                        IFieldInitializerOperation init when init.InitializedFields.Contains((member as IFieldSymbol)!, SymbolEqualityComparer.Default) => init.Value,
                        IPropertyInitializerOperation init when init.InitializedProperties.Contains((member as IPropertySymbol)!, SymbolEqualityComparer.Default) => init.Value,
                        ISimpleAssignmentOperation assignment when IsMember(assignment.Target, member) => assignment.Value,
                        ICoalesceAssignmentOperation assignment when IsMember(assignment.Target, member) => assignment.Value,
                        _ => null,
                    };

                    if (value is not null)
                    {
                        values.Add(value);
                    }
                }
            }
        }

        return values.ToImmutable();
    }

    /// <summary>
    /// What a call through a delegate runs: the lambdas, method groups and compiled queries assigned to the local
    /// (in the method), or to the field or auto-property (anywhere in its type) the delegate is read from. Empty for a
    /// delegate from anywhere else (a parameter, a method's result).
    /// </summary>
    /// <param name="target">The delegate expression called.</param>
    public StoredCallbacks CallbacksOf(IOperation? target)
    {
        var values = Operations.StripConversions(target) switch
        {
            ILocalReferenceOperation local => Operations.AssignedValues(local.Local, local),
            IFieldReferenceOperation field => AssignedValues(field.Field),
            IPropertyReferenceOperation property when IsAutoProperty(property.Property) => AssignedValues(property.Property),
            _ => ImmutableArray<IOperation>.Empty,
        };

        var callbacks = new StoredCallbacks([], [], []);
        foreach (var value in values)
        {
            if (Operations.AsLambda(value) is { } lambda)
            {
                callbacks.Lambdas.Add(lambda);
            }
            else if (Operations.AsMethodGroup(value) is { } method)
            {
                callbacks.Methods.Add(method);
            }
            else if (Operations.StripConversions(value) is IInvocationOperation { TargetMethod: { Name: "CompileQuery" or "CompileAsyncQuery", ContainingType: { } ef } } compile
                     && Model.Symbols.FullName(ef) == "Microsoft.EntityFrameworkCore.EF")
            {
                callbacks.CompiledQueries.Add(compile);
            }
        }

        return callbacks;
    }

    /// <summary>Whether an assignment target is the given field or property.</summary>
    /// <param name="target">The target.</param>
    /// <param name="member">The member.</param>
    private static bool IsMember(IOperation target, ISymbol member)
        => target switch
        {
            IFieldReferenceOperation field => SymbolEqualityComparer.Default.Equals(field.Field.OriginalDefinition, member),
            IPropertyReferenceOperation property => SymbolEqualityComparer.Default.Equals(property.Property.OriginalDefinition, member),
            _ => false,
        };

    /// <summary>Whether a call cannot be followed: it is dispatched or external and no body for it is in the compilation.</summary>
    /// <param name="method">The method called.</param>
    public bool IsOpaque(IMethodSymbol method) => TargetsOf(method).IsEmpty;

    /// <summary>Searches for a witness with an explicit remaining depth; results are cached per argument shape.</summary>
    /// <param name="method">The method called.</param>
    /// <param name="arguments">The argument sources.</param>
    /// <param name="kind">Query or save.</param>
    /// <param name="depth">How many more calls to follow.</param>
    /// <param name="dispatched">Whether the call is dispatched; a base call runs only the method's own body.</param>
    private Witness? FindWitness(IMethodSymbol method, ImmutableArray<SourceInfo> arguments, EffectKind kind, int depth, bool dispatched)
    {
        var definition = method.OriginalDefinition;
        if (Settings.IgnoredMethods.MatchesMethod(definition))
        {
            return null;
        }

        if (kind == EffectKind.Query && Settings.QueryMethods.MatchesMethod(definition))
        {
            return new Witness(EffectKind.Query, definition.Name, ImmutableArray<string>.Empty, QueryShape.Declared(definition.Name), null);
        }

        var key = new WitnessKey(definition, ArgumentShape(arguments), kind, depth, dispatched);
        if (_witnesses.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var witness = SearchWitness(TargetsOf(definition, dispatched), arguments, kind, depth);
        _witnesses.TryAdd(key, witness);
        return witness;
    }

    /// <summary>Looks through the summaries of every method the call can run.</summary>
    /// <param name="targets">The methods the call can run.</param>
    /// <param name="arguments">The argument sources.</param>
    /// <param name="kind">Query or save.</param>
    /// <param name="depth">How many more calls to follow.</param>
    private Witness? SearchWitness(ImmutableArray<IMethodSymbol> targets, ImmutableArray<SourceInfo> arguments, EffectKind kind, int depth)
    {
        foreach (var target in targets)
        {
            if (SearchSummary(Summary(target), target, arguments, kind, depth) is { } witness)
            {
                return witness;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether every method a dispatched call can run makes a round trip of the given kind. A loop
    /// over a set of different implementations (checks, plugins, strategies) calls each of them once,
    /// so it repeats a round trip per element only when all of them make one.
    /// </summary>
    /// <param name="method">The interface or virtual method called.</param>
    /// <param name="arguments">The argument sources.</param>
    /// <param name="kind">Query or save.</param>
    public bool EveryTargetMakes(IMethodSymbol method, ImmutableArray<SourceInfo> arguments, EffectKind kind)
    {
        var targets = TargetsOf(method);
        foreach (var target in targets)
        {
            if (FindWitness(target, arguments, kind) is null)
            {
                return false;
            }
        }

        return !targets.IsEmpty;
    }

    /// <summary>
    /// Proof that running a body that is not a method of its own (a lambda kept in a local) makes a
    /// round trip. Its summary is built on the spot and not cached: the lambda belongs to the block
    /// being analysed.
    /// </summary>
    /// <param name="body">The lambda's body.</param>
    /// <param name="symbol">The lambda's symbol.</param>
    /// <param name="arguments">What each argument is, by parameter ordinal.</param>
    /// <param name="kind">Query or save.</param>
    public Witness? FindWitnessInBody(IOperation body, IMethodSymbol symbol, ImmutableArray<SourceInfo> arguments, EffectKind kind)
        => SearchSummary(new SummaryBuilder(this, body, symbol).Build(), symbol, arguments, kind, Settings.MaxCallDepth);

    /// <summary>
    /// Whether a call can come back to <paramref name="to"/> through the calls in the summaries, within
    /// <paramref name="depth"/> calls: the two methods recurse through each other. A base call (<c>base.GetAsync(id)</c>
    /// in the override of <c>GetAsync</c>) runs only the base body, which never comes back to the override.
    /// </summary>
    /// <param name="call">The call.</param>
    /// <param name="to">The method to come back to.</param>
    /// <param name="depth">How many calls to follow.</param>
    public bool Reaches(IInvocationOperation call, IMethodSymbol to, int depth)
        => Reaches(TargetsOf(call.TargetMethod, call.IsVirtual), to, depth, new Dictionary<IMethodSymbol, int>(SymbolEqualityComparer.Default));

    /// <summary>
    /// Whether running one of the given methods can lead to <paramref name="to"/> within <paramref name="depth"/> calls. A
    /// method reached again with no more depth left than before is not followed again: what it can reach was already
    /// looked at, so the search stays linear in the call graph instead of trying every path.
    /// </summary>
    /// <param name="targets">The methods a call can run.</param>
    /// <param name="to">The method to come back to.</param>
    /// <param name="depth">How many calls to follow.</param>
    /// <param name="followed">The methods whose calls were followed, with the depth that was left.</param>
    private bool Reaches(ImmutableArray<IMethodSymbol> targets, IMethodSymbol to, int depth, Dictionary<IMethodSymbol, int> followed)
    {
        foreach (var target in targets)
        {
            if (SymbolEqualityComparer.Default.Equals(target, to.OriginalDefinition))
            {
                return true;
            }

            if (depth <= 0 || followed.TryGetValue(target, out var left) && left >= depth)
            {
                continue;
            }

            followed[target] = depth;
            foreach (var call in Summary(target).Calls)
            {
                if (!SymbolEqualityComparer.Default.Equals(call.Callee, target) && Reaches(TargetsOf(call.Callee, call.Dispatched), to, depth - 1, followed))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Looks for a round trip of the given kind in one summary and, below the depth limit, in its calls.</summary>
    /// <param name="summary">The summary.</param>
    /// <param name="target">The method (or lambda) it summarises.</param>
    /// <param name="arguments">The argument sources.</param>
    /// <param name="kind">Query or save.</param>
    /// <param name="depth">How many more calls to follow.</param>
    private Witness? SearchSummary(MethodSummary summary, IMethodSymbol target, ImmutableArray<SourceInfo> arguments, EffectKind kind, int depth)
    {
        foreach (var effect in summary.Effects)
        {
            if (effect.Kind != kind)
            {
                continue;
            }

            var runs = effect.Source.Kind == OriginKind.Ef
                       || (effect.Source.Kind == OriginKind.Parameter && Argument(arguments, effect.Source.Parameter).IsEfQuery);
            if (runs)
            {
                return new Witness(kind, effect.Name, ImmutableArray.Create(Model.Symbols.Display(target)), effect.Shape, effect.Location);
            }
        }

        if (depth <= 0)
        {
            return null;
        }

        foreach (var call in summary.Calls)
        {
            if (SymbolEqualityComparer.Default.Equals(call.Callee, target))
            {
                continue;
            }

            var mapped = MapArguments(call.Arguments, arguments);
            if (FindWitness(call.Callee, mapped, kind, depth - 1, call.Dispatched) is { } inner)
            {
                return inner.Through(Model.Symbols.Display(target));
            }
        }

        return null;
    }

    /// <summary>Collects navigation reads on a parameter, following the parameter into other calls.</summary>
    /// <param name="targets">The methods a call can run.</param>
    /// <param name="ordinal">The parameter ordinal in them.</param>
    /// <param name="elements">Whether the method receives elements of the original parameter, so all its reads are per element.</param>
    /// <param name="depth">How many more calls to follow.</param>
    /// <param name="search">Where the reads go, and which methods were followed.</param>
    private void CollectNavigationReads(ImmutableArray<IMethodSymbol> targets, int ordinal, bool elements, int depth, NavigationReadSearch search)
    {
        foreach (var target in targets)
        {
            // Reached again with no more depth left, the same parameter and the same element flag, a method adds nothing new.
            if (!search.Follow(target, ordinal, elements, depth))
            {
                continue;
            }

            var summary = Summary(target);
            foreach (var read in summary.NavigationReads)
            {
                var found = elements ? read with { PerElement = true } : read;
                if (read.Parameter == ordinal && search.Found.Add(found))
                {
                    search.Builder.Add(found);
                }
            }

            if (depth <= 0)
            {
                continue;
            }

            foreach (var call in summary.Calls)
            {
                for (var i = 0; i < call.Arguments.Length; i++)
                {
                    // The parameter handed on as it is keeps its meaning; an element of it (a foreach
                    // variable, a method group over it) makes every read in the callee a per-element read.
                    var argument = call.Arguments[i];
                    if (argument.Kind == OriginKind.Parameter && argument.Parameter == ordinal && argument.Path.Length == 0
                        && !SymbolEqualityComparer.Default.Equals(call.Callee, target))
                    {
                        CollectNavigationReads(TargetsOf(call.Callee, call.Dispatched), i, elements || argument.IsElement, depth - 1, search);
                    }
                }
            }
        }
    }

    /// <summary>Finds the bodies a call can run.</summary>
    /// <param name="method">The method's original definition.</param>
    private ImmutableArray<IMethodSymbol> FindTargets(IMethodSymbol method)
    {
        var builder = ImmutableArray.CreateBuilder<IMethodSymbol>();
        if (!method.IsAbstract && HasSummary(method))
        {
            builder.Add(method);
        }

        if (!Model.Symbols.IsDispatched(method) || method.IsSealed)
        {
            return builder.ToImmutable();
        }

        var candidates = method.ContainingType.TypeKind == TypeKind.Interface
            ? Implementers(method.ContainingType.OriginalDefinition)
            : _overriding.Value;
        foreach (var type in candidates)
        {
            if (builder.Count >= MaxImplementations)
            {
                break;
            }

            if (Implementation(type, method) is { } implementation
                && !builder.Contains(implementation, SymbolEqualityComparer.Default)
                && HasSummary(implementation))
            {
                builder.Add(implementation);
            }
        }

        return builder.ToImmutable();
    }

    /// <summary>
    /// The candidate types implementing an interface, directly, through a base type or another interface. Kept for the
    /// next compilation with the same declarations: the test resolves the base types of every type.
    /// </summary>
    /// <param name="iface">The interface definition.</param>
    private ImmutableArray<INamedTypeSymbol> Implementers(INamedTypeSymbol iface)
        => _facts.Implementers(Compilation, _implementingCandidates, iface);

    /// <summary>The member of <paramref name="type"/> that a call to <paramref name="method"/> runs on an instance of it.</summary>
    /// <param name="type">A source type.</param>
    /// <param name="method">An interface, abstract or virtual method.</param>
    private static IMethodSymbol? Implementation(INamedTypeSymbol type, IMethodSymbol method)
    {
        if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct) || SymbolEqualityComparer.Default.Equals(type, method.ContainingType))
        {
            return null;
        }

        if (method.ContainingType.TypeKind == TypeKind.Interface)
        {
            foreach (var iface in type.AllInterfaces)
            {
                if (!SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, method.ContainingType.OriginalDefinition))
                {
                    continue;
                }

                foreach (var member in iface.GetMembers(method.Name))
                {
                    if (member is IMethodSymbol candidate
                        && SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, method)
                        && type.FindImplementationForInterfaceMember(candidate) is IMethodSymbol implementation)
                    {
                        return implementation.OriginalDefinition;
                    }
                }
            }

            return null;
        }

        foreach (var member in type.GetMembers(method.Name))
        {
            if (member is not IMethodSymbol { IsOverride: true } candidate)
            {
                continue;
            }

            for (var overridden = candidate.OverriddenMethod; overridden is not null; overridden = overridden.OverriddenMethod)
            {
                if (SymbolEqualityComparer.Default.Equals(overridden.OriginalDefinition, method))
                {
                    return candidate.OriginalDefinition;
                }
            }
        }

        return null;
    }

    /// <summary>Builds a method's summary from its declaration, or reads it from its assembly's summaries.</summary>
    /// <param name="method">The method.</param>
    private MethodSummary BuildSummary(IMethodSymbol method)
    {
        // A partial method's body is in its implementation part, often generated (a Mapperly mapper).
        method = method.PartialImplementationPart ?? method;
        if (!Model.Symbols.HasSource(method, Compilation))
        {
            return _referenced.Value.TryGet(method, out var facts) ? SummaryExport.Decode(facts) : MethodSummary.Empty;
        }

        var syntax = method.DeclaringSyntaxReferences[0].GetSyntax();

#pragma warning disable RS1030 // Summaries need the bodies of other methods; each model is fetched once per summary and the summary is cached.
        var model = Compilation.GetSemanticModel(syntax.SyntaxTree);
#pragma warning restore RS1030
        var body = syntax switch
        {
            ArrowExpressionClauseSyntax arrow => model.GetOperation(arrow) ?? model.GetOperation(arrow.Expression),
            _ => model.GetOperation(syntax),
        };

        return body is null ? MethodSummary.Empty : new SummaryBuilder(this, body, method).Build();
    }

    /// <summary>Every named type declared in source, nested ones included.</summary>
    private ImmutableArray<INamedTypeSymbol> CollectSourceTypes() => Model.Symbols.AllTypes(Compilation.Assembly.GlobalNamespace).ToImmutableArray();

    /// <summary>
    /// Whether a method is passed by name to a minimal API mapping as its handler
    /// (<c>app.MapGet("/orders", GetOrders)</c>); what it returns is serialized like an action's result.
    /// </summary>
    /// <param name="symbol">The block owner.</param>
    public bool IsEndpointHandler(ISymbol symbol)
        => symbol is IMethodSymbol { MethodKind: MethodKind.Ordinary } method && _endpointHandlers.Value.Contains(method.OriginalDefinition);

    /// <summary>
    /// The methods passed as method groups to <c>MapGet</c>, <c>MapPost</c>, ... in the compilation. Only files
    /// that call one of those get a semantic model, and only their <c>Map*</c> calls are bound.
    /// </summary>
    private ImmutableHashSet<IMethodSymbol> CollectEndpointHandlers()
    {
        var builder = ImmutableHashSet.CreateBuilder<IMethodSymbol>(SymbolEqualityComparer.Default);
        foreach (var tree in Compilation.SyntaxTrees)
        {
            var mappings = SyntaxIndex.Of(tree).Mappings;
            if (mappings.IsEmpty)
            {
                continue;
            }

#pragma warning disable RS1030 // Handlers are declared elsewhere than where they are mapped; this runs once per compilation.
            var model = Compilation.GetSemanticModel(tree);
#pragma warning restore RS1030
            foreach (var mapping in mappings)
            {
                if (model.GetOperation(mapping) is not IInvocationOperation invocation
                    || !BlockAnalyzer.IsEndpointMapping(invocation.TargetMethod))
                {
                    continue;
                }

                foreach (var argument in invocation.Arguments)
                {
                    if (Operations.AsMethodGroup(argument.Value) is { } handler)
                    {
                        builder.Add(handler.OriginalDefinition);
                    }
                }
            }
        }

        return builder.ToImmutable();
    }

    /// <summary>
    /// The types an interface or virtual call can land in: the source types the syntax test allows, and the types of
    /// referenced assemblies that wrote complete summaries. The test keeps a call from making the compiler resolve the base
    /// types or build the members of every type: an interface call can only land in a type with a base list, a virtual
    /// call only in a type that declares an override. Collected only when needed: an interface call answered from the
    /// interface map kept in <see cref="DeclarationFacts"/> never asks for them.
    /// </summary>
    /// <param name="mayLand">Whether a call can land in a source type, by its syntax.</param>
    private ImmutableArray<INamedTypeSymbol> CollectCandidateTypes(Func<INamedTypeSymbol, bool> mayLand)
    {
        var candidates = ImmutableArray.CreateBuilder<INamedTypeSymbol>();
        foreach (var type in _sourceTypes.Value)
        {
            if (mayLand(type))
            {
                candidates.Add(type);
            }
        }

        candidates.AddRange(_completeTypes.Value);
        return candidates.ToImmutable();
    }

    /// <summary>The types of the referenced assemblies that wrote complete summaries: any call can land in them.</summary>
    private ImmutableArray<INamedTypeSymbol> CollectCompleteTypes()
    {
        var types = ImmutableArray.CreateBuilder<INamedTypeSymbol>();
        foreach (var assembly in _referenced.Value.CompleteAssemblies)
        {
            types.AddRange(Model.Symbols.AllTypes(assembly.GlobalNamespace));
        }

        return types.ToImmutable();
    }

    /// <summary>Replaces parameter sources in a callee's arguments with the caller's arguments.</summary>
    /// <param name="calleeArguments">The arguments a summarised method passes on.</param>
    /// <param name="callerArguments">What the summarised method was called with.</param>
    private static ImmutableArray<SourceInfo> MapArguments(ImmutableArray<SourceInfo> calleeArguments, ImmutableArray<SourceInfo> callerArguments)
    {
        var builder = ImmutableArray.CreateBuilder<SourceInfo>(calleeArguments.Length);
        foreach (var argument in calleeArguments)
        {
            builder.Add(argument.Kind == OriginKind.Parameter ? Argument(callerArguments, argument.Parameter) : argument);
        }

        return builder.MoveToImmutable();
    }

    /// <summary>The argument with the given ordinal, or unknown when there is none.</summary>
    /// <param name="arguments">The arguments.</param>
    /// <param name="ordinal">The ordinal.</param>
    private static SourceInfo Argument(ImmutableArray<SourceInfo> arguments, int ordinal)
        => ordinal >= 0 && ordinal < arguments.Length ? arguments[ordinal] : SourceInfo.Unknown;

    /// <summary>A cache key for argument sources: which arguments are EF queries.</summary>
    /// <param name="arguments">The arguments.</param>
    private static long ArgumentShape(ImmutableArray<SourceInfo> arguments)
    {
        long shape = 0;
        for (var i = 0; i < arguments.Length && i < 64; i++)
        {
            if (arguments[i].IsEfQuery)
            {
                shape |= 1L << i;
            }
        }

        return shape;
    }

    /// <summary>Whether a definition is the given known type.</summary>
    /// <param name="type">A type definition.</param>
    /// <param name="known">A known symbol, possibly missing.</param>
    private static bool Is(INamedTypeSymbol? type, INamedTypeSymbol? known)
        => type is not null && known is not null && SymbolEqualityComparer.Default.Equals(type, known);

    /// <summary>One search for the navigations a parameter is read through: what it found, and what it followed.</summary>
    /// <param name="builder">Collects the reads, in the order they are found.</param>
    private sealed class NavigationReadSearch(ImmutableArray<ParameterNavigationRead>.Builder builder)
    {
        /// <summary>The methods followed, by parameter and element flag, with the depth that was left.</summary>
        private readonly Dictionary<(IMethodSymbol Method, int Ordinal, bool Elements), int> _followed = new(FollowedComparer.Instance);

        /// <summary>The reads, in the order they are found.</summary>
        public ImmutableArray<ParameterNavigationRead>.Builder Builder { get; } = builder;

        /// <summary>The reads found, for a constant-time check that one is new.</summary>
        public HashSet<ParameterNavigationRead> Found { get; } = [];

        /// <summary>Whether to follow a method: not followed yet for this parameter and flag with as much depth left.</summary>
        /// <param name="method">The method.</param>
        /// <param name="ordinal">The parameter.</param>
        /// <param name="elements">Whether its reads are per element.</param>
        /// <param name="depth">The depth left.</param>
        public bool Follow(IMethodSymbol method, int ordinal, bool elements, int depth)
        {
            var key = (method, ordinal, elements);
            if (_followed.TryGetValue(key, out var left) && left >= depth)
            {
                return false;
            }

            _followed[key] = depth;
            return true;
        }

        /// <summary>Compares the keys with the symbol comparer.</summary>
        private sealed class FollowedComparer : IEqualityComparer<(IMethodSymbol Method, int Ordinal, bool Elements)>
        {
            /// <summary>The one instance.</summary>
            public static FollowedComparer Instance { get; } = new();

            /// <inheritdoc />
            public bool Equals((IMethodSymbol Method, int Ordinal, bool Elements) x, (IMethodSymbol Method, int Ordinal, bool Elements) y)
                => x.Ordinal == y.Ordinal && x.Elements == y.Elements && SymbolEqualityComparer.Default.Equals(x.Method, y.Method);

            /// <inheritdoc />
            public int GetHashCode((IMethodSymbol Method, int Ordinal, bool Elements) obj)
                => unchecked((SymbolEqualityComparer.Default.GetHashCode(obj.Method) * 31 + obj.Ordinal) * 2 + (obj.Elements ? 1 : 0));
        }
    }

    /// <summary>The key of a cached witness search.</summary>
    /// <param name="Method">The method called.</param>
    /// <param name="Arguments">Which arguments are EF queries.</param>
    /// <param name="Kind">Query or save.</param>
    /// <param name="Depth">The remaining depth.</param>
    /// <param name="Dispatched">Whether the call is dispatched or runs the method's own body only.</param>
    private readonly record struct WitnessKey(IMethodSymbol Method, long Arguments, EffectKind Kind, int Depth, bool Dispatched)
    {
        /// <inheritdoc/>
        public bool Equals(WitnessKey other)
            => SymbolEqualityComparer.Default.Equals(Method, other.Method)
               && Arguments == other.Arguments
               && Kind == other.Kind
               && Depth == other.Depth
               && Dispatched == other.Dispatched;

        /// <inheritdoc/>
        public override int GetHashCode()
            => (((SymbolEqualityComparer.Default.GetHashCode(Method) * 31 + Arguments.GetHashCode()) * 31 + (int)Kind) * 31 + Depth) * 2 + (Dispatched ? 1 : 0);
    }
}
