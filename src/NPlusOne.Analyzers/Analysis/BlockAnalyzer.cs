using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using NPlusOne.Analyzers.Model;

namespace NPlusOne.Analyzers.Analysis;

/// <summary>
/// Analyses one operation block (a method, accessor or lambda-bearing initializer): reports round
/// trips made once per element where they happen, and lazy loads once the whole block has been
/// seen, one per navigation and iteration.
/// </summary>
/// <param name="session">The compilation-wide analysis state.</param>
/// <param name="owner">The symbol that owns the block.</param>
internal sealed partial class BlockAnalyzer(Session session, ISymbol owner)
{
    private readonly SourceTracer _tracer = new(session, owner as IMethodSymbol);
    private readonly IterationFinder _iterations = new(session);
    private readonly LazyLoads _lazy = new(session);
    private readonly Dictionary<(IOperation Region, ISymbol Root, string Path), Diagnostic> _lazyLoads = [];
    private readonly object _gate = new();

    /// <summary>
    /// Whether the owner is an endpoint whose returned entities the serializer will walk: an MVC action, a
    /// SignalR hub method, or a method passed to a minimal API mapping as its handler. Only lazy loads make a
    /// serialized result costly, so without lazy loading the endpoint mappings are never collected.
    /// </summary>
    private readonly bool _isAction = session.Entities.AnyLazyLoading
                                      && (IsAction(owner, session.Symbols) || IsHubMethod(owner, session.Symbols) || session.IsEndpointHandler(owner))
                                      && !HasEnableQuery(owner);

    /// <summary>Handles an invocation: a round trip, a call that leads to one, or a minimal API mapping.</summary>
    /// <param name="context">The operation context.</param>
    public void OnInvocation(OperationAnalysisContext context)
    {
        var invocation = (IInvocationOperation)context.Operation;
        if (Operations.IsInExpressionTree(invocation))
        {
            CheckClientProjection(context, invocation);
            return;
        }

        CheckImplicitMembers(invocation);
        CheckMethodGroups(context, invocation);

        var call = QueryCalls.Classify(invocation, session.Symbols);
        switch (call.Kind)
        {
            case QueryCallKind.Execute when _tracer.Trace(call.Source).IsEfQuery:
            case QueryCallKind.ExecuteAlways:
                ReportDirect(context, invocation, EffectKind.Query, call.Kind);
                break;
            case QueryCallKind.Save:
                ReportDirect(context, invocation, EffectKind.Save, call.Kind);
                break;
            case QueryCallKind.None:
                OnUserCall(context, invocation);
                break;
        }
    }

    /// <summary>
    /// A method of the project called in a query's final projection (<c>db.Orders.Select(o =&gt; Describe(o))</c>):
    /// EF Core cannot translate it, so it materializes each row and calls the method on the client, once per row. The
    /// queries the method runs repeat per row (NP0001), and so do the lazy navigations it reads on the row's entities
    /// (NP0002). Only the selector of the outermost <c>Queryable.Select</c> is evaluated on the client; anywhere else
    /// EF Core refuses the query instead.
    /// </summary>
    /// <param name="context">The operation context.</param>
    /// <param name="invocation">A call inside an expression tree.</param>
    private void CheckClientProjection(OperationAnalysisContext context, IInvocationOperation invocation)
    {
        var method = invocation.TargetMethod;
        if (!session.HasSummary(method) || ClientProjection(invocation) is not { } projection)
        {
            return;
        }

        var (selector, select) = projection;
        var rows = _tracer.Trace(select.Arguments[0].Value).Element();
        var iteration = new Iteration(IterationKind.Callback, selector, $"'{Operations.Describe(select.Arguments[0].Value)}', whose final Select EF Core runs on the client", false);
        var callee = Model.Symbols.Display(method);
        var location = Operations.ReportLocation(invocation);
        var arguments = ImmutableArray.CreateBuilder<SourceInfo>(method.Parameters.Length);
        for (var i = 0; i < method.Parameters.Length; i++)
        {
            arguments.Add(SourceInfo.Unknown);
        }

        foreach (var argument in invocation.Arguments)
        {
            if (argument.Parameter is { } parameter && parameter.Ordinal < arguments.Count)
            {
                arguments[parameter.Ordinal] = Operations.StripConversions(argument.Value) is IParameterReferenceOperation row
                                               && SymbolEqualityComparer.Default.Equals(row.Parameter.ContainingSymbol, selector.Symbol)
                    ? rows
                    : _tracer.Trace(argument.Value);
            }
        }

        var traced = arguments.MoveToImmutable();
        if (session.FindWitness(method, traced, EffectKind.Query) is { } query)
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.QueryPerElement, location, AdditionalLocations(query), Messages.QueryPerElement(query, iteration, callee)));
        }

        foreach (var argument in invocation.Arguments)
        {
            if (argument.Parameter is not { } parameter || traced[parameter.Ordinal] is not { Kind: OriginKind.Ef } element || argument.Value.Type is not { } type)
            {
                continue;
            }

            foreach (var read in session.NavigationReadsOf(method, parameter.Ordinal))
            {
                if (!read.PerElement && LoadsLazily(element, type, read.Path))
                {
                    var full = SourceTracer.Join(element.Path, read.Path);
                    AddLazyLoad((selector, method, full), location, element.Root, Messages.LazyLoadPerElement(full, iteration, callee));
                }
            }
        }
    }

    /// <summary>
    /// The selector lambda of the outermost <c>Queryable.Select</c> an expression-tree operation is in, with that call;
    /// null when the outermost expression lambda is passed to anything else.
    /// </summary>
    /// <param name="operation">An operation inside an expression tree.</param>
    private static (IAnonymousFunctionOperation Selector, IInvocationOperation Select)? ClientProjection(IOperation operation)
    {
        IAnonymousFunctionOperation? outermost = null;
        for (var current = operation.Parent; current is not null; current = current.Parent)
        {
            if (current is IAnonymousFunctionOperation lambda && Operations.IsExpressionTree(lambda))
            {
                outermost = lambda;
            }
        }

        if (outermost is null || CallbackSite.Of(outermost) is not { } site
            || site.Invocation.TargetMethod is not { Name: "Select", ContainingType: { } type } || Model.Symbols.FullName(type) != "System.Linq.Queryable"
            || site.Invocation.Arguments.Length == 0)
        {
            return null;
        }

        return (outermost, site.Invocation);
    }

    /// <summary>A <c>foreach</c> directly over an EF query inside another iteration runs the query once per outer element.</summary>
    /// <param name="context">The operation context.</param>
    public void OnForEach(OperationAnalysisContext context)
    {
        if (context.Operation is not IForEachLoopOperation loop || Operations.IsInExpressionTree(loop) || !_tracer.Trace(loop.Collection).IsEfQuery)
        {
            return;
        }

        if (_iterations.Find(loop) is not { Kind: not IterationKind.ExpressionTree } iteration)
        {
            return;
        }

        var shape = new QueryShape(ShapeKind.Rows, "foreach", null, null, false);
        if (Exemptions.IsIntended(loop, iteration, shape))
        {
            return;
        }

        var witness = new Witness(EffectKind.Query, "foreach", ImmutableArray<string>.Empty, shape, null);
        context.ReportDiagnostic(Diagnostic.Create(Descriptors.QueryPerElement, loop.Collection.Syntax.GetLocation(), Messages.QueryPerElement(witness, iteration, null)));
    }

    /// <summary>Collects a lazy navigation read on an element of an iteration.</summary>
    /// <param name="context">The operation context.</param>
    public void OnPropertyReference(OperationAnalysisContext context)
    {
        var property = (IPropertyReferenceOperation)context.Operation;
        CheckGetter(context, property);
        if (session.Entities.AsNavigation(property.Property) is null
            && property.Property.GetMethod is { } getter
            && session.Entities.IsEntity(property.Property.ContainingType)
            && !Operations.IsInExpressionTree(property))
        {
            // A computed property of the entity (ParcelWeight => Parcels.Sum(...)).
            CheckInstanceMember(property, property.Instance, getter, property.Syntax.GetLocation());
            return;
        }

        if (session.Entities.AsNavigation(property.Property) is not { IsLazy: true }
            || LazyLoads.IsWriteOrLongerPath(property, session.Entities)
            || Operations.IsInExpressionTree(property))
        {
            return;
        }

        var element = _lazy.ElementOf(property.Instance);
        var walk = element is null ? ChainWalk(property) : null;
        var iteration = element?.Iteration ?? walk;
        if (iteration is null || LazyLoadingTurnedOffBefore(property))
        {
            return;
        }

        // Split o.Customer.Address into the element (o) and the navigation path read on it.
        var segments = new List<string>();
        IOperation? current = property;
        while (Operations.StripConversions(current) is IPropertyReferenceOperation navigation && session.Entities.AsNavigation(navigation.Property) is not null)
        {
            segments.Insert(0, navigation.Property.Name);
            current = navigation.Instance;
        }

        if (LoadedExplicitlyBefore(property, current, segments[0]))
        {
            return;
        }

        var owner = walk is null ? _tracer.Trace(current) : WalkStart((ILocalReferenceOperation)Operations.StripConversions(current)!);
        var relative = string.Join(".", segments);
        if (current?.Type is not { } elementType || !LoadsLazily(owner, elementType, relative))
        {
            return;
        }

        var path = SourceTracer.Join(owner.Path, relative);
        var root = element?.Root ?? ((ILocalReferenceOperation)Operations.StripConversions(property.Instance)!).Local;
        AddLazyLoad((iteration.Region, root, path), property.Syntax.GetLocation(), owner.Root, Messages.LazyLoadPerElement(path, iteration, null));
    }

    /// <summary>
    /// The getter a property or indexer read runs, when it may make a round trip: a getter with a body in the project
    /// (or a referenced project's summary), read rather than assigned, and not a navigation. Null otherwise.
    /// </summary>
    /// <param name="property">The property reference.</param>
    /// <param name="session">The analysis state.</param>
    internal static IMethodSymbol? RunGetter(IPropertyReferenceOperation property, Session session)
        => property.Property.GetMethod is { } getter
           && !(property.Parent is ISimpleAssignmentOperation assignment && assignment.Target == property)
           && session.Entities.AsNavigation(property.Property) is null
           && !Session.IsAutoProperty(property.Property)
           && session.HasSummary(getter)
            ? getter
            : null;

    /// <summary>
    /// NP0001/NP0003: reading a property or an indexer runs its getter; one that queries (<c>row.OrderCount</c>,
    /// <c>lookup[id]</c>) does so once per element when read in an iteration.
    /// </summary>
    /// <param name="context">The operation context.</param>
    /// <param name="property">The property reference.</param>
    private void CheckGetter(OperationAnalysisContext context, IPropertyReferenceOperation property)
    {
        if (RunGetter(property, session) is not { } getter || _iterations.Find(property) is not { Kind: not IterationKind.ExpressionTree } iteration)
        {
            return;
        }

        var arguments = ImmutableArray.CreateBuilder<SourceInfo>(getter.Parameters.Length);
        for (var i = 0; i < getter.Parameters.Length; i++)
        {
            arguments.Add(i < property.Arguments.Length ? _tracer.Trace(property.Arguments[i].Value) : SourceInfo.Unknown);
        }

        var traced = arguments.MoveToImmutable();
        var callee = Model.Symbols.Display(getter);
        var location = property.Syntax.GetLocation();
        if (session.FindWitness(getter, traced, EffectKind.Query) is { } query && !Exemptions.IsIntended(property, iteration, query.Shape))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.QueryPerElement, location, AdditionalLocations(query), Messages.QueryPerElement(query, iteration, callee)));
        }

        if (session.FindWitness(getter, traced, EffectKind.Save) is { } save && !Exemptions.IsIntended(property, iteration, save.Shape))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.SavePerElement, location, AdditionalLocations(save), Messages.SavePerElement(save, iteration, callee)));
        }
    }

    /// <summary>A return from an MVC action: the serializer reads every navigation of returned entities.</summary>
    /// <param name="context">The operation context.</param>
    public void OnReturn(OperationAnalysisContext context)
    {
        if (!_isAction || context.Operation is not IReturnOperation { ReturnedValue: { } value } returned || !BelongsToOwner(returned))
        {
            return;
        }

        if (returned.Kind == OperationKind.YieldReturn)
        {
            CheckStreamedItem(context, value);
            return;
        }

        CheckEndpointResult(context, value);
    }

    /// <summary>
    /// NP0005 for one item an endpoint streams: <c>yield return</c> in an action or hub method returning
    /// <c>IAsyncEnumerable&lt;T&gt;</c>, or a write to the channel a hub method returns the reader of. The serializer writes
    /// every item, so the navigations of each are loaded once per item.
    /// </summary>
    /// <param name="context">The operation context.</param>
    /// <param name="item">The streamed item.</param>
    private void CheckStreamedItem(OperationAnalysisContext context, IOperation item)
    {
        if (session.Entities.AnyLazyLoading)
        {
            CheckSerializedValue(context, new SerializedSite(item, "The endpoint streams", JsonSerializerKind.SystemTextJson, Item: true), item, "", 0);
        }
    }

    /// <summary>
    /// A hub method that returns a <c>ChannelReader&lt;T&gt;</c> streams what is written to the channel
    /// (<c>channel.Writer.WriteAsync(order)</c>, <c>TryWrite</c>), in the method or a lambda in it.
    /// </summary>
    /// <param name="context">The operation context.</param>
    /// <param name="invocation">The call.</param>
    private void CheckChannelWrite(OperationAnalysisContext context, IInvocationOperation invocation)
    {
        if (invocation.TargetMethod is not { Name: "WriteAsync" or "TryWrite", ContainingType: { } writer } || invocation.Arguments.Length == 0
            || Model.Symbols.FullName(writer.OriginalDefinition) != "System.Threading.Channels.ChannelWriter"
            || !IsHubMethod(owner, session.Symbols)
            || owner is not IMethodSymbol { ReturnType: INamedTypeSymbol returned } || Model.Symbols.FullName(returned.OriginalDefinition) != "System.Threading.Channels.ChannelReader")
        {
            return;
        }

        CheckStreamedItem(context, invocation.Arguments[0].Value);
    }

    /// <summary>Reports the lazy loads collected for the block, earliest first.</summary>
    /// <param name="context">The block end context.</param>
    public void OnBlockEnd(OperationBlockAnalysisContext context)
    {
        lock (_gate)
        {
            foreach (var diagnostic in _lazyLoads.Values.OrderBy(d => d.Location.SourceSpan.Start))
            {
                context.ReportDiagnostic(diagnostic);
            }
        }
    }

    /// <summary>Reports a round trip made right at the call, when it runs once per element.</summary>
    /// <param name="context">The operation context.</param>
    /// <param name="invocation">The executing call.</param>
    /// <param name="kind">Query or save.</param>
    /// <param name="callKind">How the call was classified.</param>
    private void ReportDirect(OperationAnalysisContext context, IInvocationOperation invocation, EffectKind kind, QueryCallKind callKind)
    {
        if (_iterations.Find(invocation) is not { Kind: not IterationKind.ExpressionTree } iteration)
        {
            return;
        }

        var shape = QueryShapes.Of(invocation, callKind);
        if (Exemptions.IsIntended(invocation, iteration, shape))
        {
            return;
        }

        var witness = new Witness(kind, invocation.TargetMethod.Name, ImmutableArray<string>.Empty, shape, null);
        var location = Operations.ReportLocation(invocation);
        if (kind == EffectKind.Query)
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.QueryPerElement, location, Messages.QueryPerElement(witness, iteration, null)));
            return;
        }

        // A SaveChanges statement directly in the body of a foreach over a collection can be moved after the loop by the
        // code fix, when everything it uses is declared before the loop (a context created per element is not).
        var properties = iteration.Region is IForEachLoopOperation && !UsesLocalsOf(invocation, iteration.Region)
            ? ImmutableDictionary<string, string?>.Empty.Add(DiagnosticProperties.MovableSave, "true")
            : ImmutableDictionary<string, string?>.Empty;
        context.ReportDiagnostic(Diagnostic.Create(Descriptors.SavePerElement, location, null, properties, Messages.SavePerElement(witness, iteration, null)));
    }

    /// <summary>
    /// A call to a method that is not a query operator: follows it through summaries, reports
    /// method groups passed to iterating methods, recursion, mappers that lazy-load, and minimal API
    /// handlers.
    /// </summary>
    /// <param name="context">The operation context.</param>
    /// <param name="invocation">The call.</param>
    private void OnUserCall(OperationAnalysisContext context, IInvocationOperation invocation)
    {
        var method = invocation.TargetMethod;
        if (method.MethodKind == MethodKind.DelegateInvoke)
        {
            CheckStoredDelegate(context, invocation);
            return;
        }

        if (IsEndpointMapping(method))
        {
            CheckMinimalApiHandler(context, invocation);
        }

        CheckSerializerCall(context, invocation);
        CheckChannelWrite(context, invocation);
        CheckMapperCall(context, invocation);

        var iteration = _iterations.Find(invocation);
        if (iteration is { Kind: IterationKind.ExpressionTree })
        {
            return;
        }

        CheckSequenceArguments(invocation);

        var arguments = TraceArguments(invocation);
        if (iteration is null)
        {
            CheckRecursion(context, invocation, arguments);
            return;
        }

        var callee = Model.Symbols.Display(method);
        var location = Operations.ReportLocation(invocation);
        var query = session.FindWitness(method, arguments, EffectKind.Query) ?? HandlerWitness(invocation, EffectKind.Query);
        var save = session.FindWitness(method, arguments, EffectKind.Save) ?? HandlerWitness(invocation, EffectKind.Save);

        // Called on each element of a set of implementations (checks, plugins, strategies): each runs
        // once, so only a round trip that every implementation makes repeats per element.
        var perImplementation = Model.Symbols.IsDispatched(method) && _lazy.ElementOf(invocation.Instance) is not null;
        if (perImplementation)
        {
            query = query is not null && session.EveryTargetMakes(method, arguments, EffectKind.Query) ? query : null;
            save = save is not null && session.EveryTargetMakes(method, arguments, EffectKind.Save) ? save : null;
        }

        if (query is not null && !Exemptions.IsIntended(invocation, iteration, query.Shape))
        {
            var recursive = owner is IMethodSymbol self && SymbolEqualityComparer.Default.Equals(method.OriginalDefinition, self.OriginalDefinition);
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.QueryPerElement, location, AdditionalLocations(query), Messages.QueryPerElement(query, iteration, callee, recursive)));
        }

        if (save is not null && !Exemptions.IsIntended(invocation, iteration, save.Shape))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.SavePerElement, location, AdditionalLocations(save), Messages.SavePerElement(save, iteration, callee)));
        }

        if (query is null && save is null && !perImplementation && IsOpaqueDataAccess(method) && !Exemptions.IsIntended(invocation, iteration, QueryShape.Declared(method.Name)))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.PossibleQueryPerElement, location, Messages.PossibleQueryPerElement(callee, iteration)));
        }

        CheckMapperArguments(invocation);
        CheckInstanceMember(invocation, invocation.Instance, invocation.TargetMethod, Operations.ReportLocation(invocation));
    }

    /// <summary>
    /// An object mapper (AutoMapper's <c>Map</c>, Mapster's <c>Adapt</c> and <c>Map</c>) given a sequence, or one element
    /// of an iteration, maps every element: see <see cref="CheckMapping"/>.
    /// </summary>
    /// <param name="context">The operation context.</param>
    /// <param name="invocation">The call.</param>
    private void CheckMapperCall(OperationAnalysisContext context, IInvocationOperation invocation)
    {
        if (Mappings.AsMapperCall(invocation) is not { } call || Operations.StripConversions(call.Source) is not { Type: { } sourceType } source)
        {
            return;
        }

        var sourceElement = session.Symbols.ElementType(sourceType);
        var element = sourceElement is null ? _lazy.ElementOf(source) : null;
        if (sourceElement is null && element is null)
        {
            return;
        }

        var entity = sourceElement ?? sourceType;
        var destination = session.Symbols.ElementType(call.Destination) ?? call.Destination;
        var iteration = element?.Iteration ?? new Iteration(IterationKind.Callback, invocation, $"'{Operations.Describe(source)}', which it maps", false);
        var key = element is null ? ((IOperation)invocation, (ISymbol)entity) : (element.Iteration.Region, element.Root);
        CheckMapping(context, invocation, new MappedElements(entity, _tracer.Trace(source), destination, call.Library, iteration, Operations.ReportLocation(invocation), key));
    }

    /// <summary>
    /// What mapping each element costs. A resolver or converter the map calls (<c>MapFrom&lt;TResolver&gt;()</c>,
    /// <c>ConvertUsing&lt;TConverter&gt;()</c>) that queries does so once per element (NP0001). With lazy loading on, the
    /// navigations the map reads on EF entities, itself or through those resolvers, load once per element: NP0002
    /// with a visible configuration (AutoMapper's <c>CreateMap</c>, Mapster's <c>NewConfig</c>), NP0007, a suggestion,
    /// when only the naming conventions say so.
    /// </summary>
    /// <param name="context">The operation context.</param>
    /// <param name="mapping">The call or method group that maps.</param>
    /// <param name="mapped">The mapped elements.</param>
    private void CheckMapping(OperationAnalysisContext context, IOperation mapping, MappedElements mapped)
    {
        var reads = session.Mappings.Reads(mapped.Source, mapped.Destination, mapped.Library);
        var reader = $"{mapped.Library} ({mapped.Source.Name} → {mapped.Destination.Name})";
        var paths = new List<string>(reads.Paths);
        var queried = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        foreach (var callback in reads.Callbacks)
        {
            if (queried.Add(callback.Method)
                && session.FindWitness(callback.Method, ImmutableArray.CreateRange(callback.Method.Parameters, static _ => SourceInfo.Unknown), EffectKind.Query) is { } query)
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.QueryPerElement, mapped.Location, AdditionalLocations(query), Messages.QueryPerElement(query, mapped.Iteration, Model.Symbols.Display(callback.Method))));
            }

            foreach (var read in session.NavigationReadsOf(callback.Method, callback.Ordinal))
            {
                var path = SourceTracer.Join(callback.Prefix, read.Path);
                if (!read.PerElement && !paths.Contains(path))
                {
                    paths.Add(path);
                }
            }
        }

        if (!session.Entities.AnyLazyLoading || !session.Entities.IsEntity(mapped.Source) || LazyLoadingTurnedOffBefore(mapping))
        {
            return;
        }

        var lazy = new List<string>();
        foreach (var path in paths)
        {
            if (LoadsLazily(mapped.Origin, mapped.Source, path))
            {
                lazy.Add(SourceTracer.Join(mapped.Origin.Path, path));
            }
        }

        if (lazy.Count == 0)
        {
            return;
        }

        if (reads.Configured)
        {
            foreach (var path in lazy)
            {
                AddLazyLoad((mapped.Key.Region, mapped.Key.Root, path), mapped.Location, mapped.Origin.Root, Messages.LazyLoadPerElement(path, mapped.Iteration, reader));
            }

            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Descriptors.ConventionMappingPerElement, mapped.Location, Messages.ConventionMappingPerElement(mapped.Source.Name, mapped.Destination.Name, lazy, mapped.Iteration)));
    }

    /// <summary>A round trip made by the handlers a mediator call runs (<c>sender.Send(new GetX(id))</c>).</summary>
    /// <param name="invocation">The call.</param>
    /// <param name="kind">Query or save.</param>
    private Witness? HandlerWitness(IInvocationOperation invocation, EffectKind kind)
    {
        foreach (var handler in session.MediatorHandlers(invocation))
        {
            var arguments = ImmutableArray.CreateRange(handler.Parameters, static _ => SourceInfo.Unknown);
            if (session.FindWitness(handler, arguments, kind) is { } witness)
            {
                return witness;
            }
        }

        return null;
    }

    /// <summary>
    /// A sequence of EF entities passed to a method that walks it and reads navigations of each
    /// element (<c>PrintCustomers(orders)</c>) lazy-loads them once per element, wherever the call is.
    /// </summary>
    /// <param name="invocation">The call.</param>
    private void CheckSequenceArguments(IInvocationOperation invocation)
    {
        foreach (var argument in invocation.Arguments)
        {
            if (argument.Parameter is null
                || session.Symbols.ElementType(Operations.StripConversions(argument.Value)?.Type) is not { } elementType
                || _tracer.Trace(argument.Value) is not { Kind: OriginKind.Ef, IsElement: false } source)
            {
                continue;
            }

            var reads = session.NavigationReadsOf(invocation.TargetMethod, argument.Parameter.Ordinal);
            if (reads.IsEmpty || LazyLoadingTurnedOffBefore(invocation))
            {
                continue;
            }

            var callee = Model.Symbols.Display(invocation.TargetMethod);
            var iteration = new Iteration(IterationKind.Callback, invocation, $"'{Operations.Describe(argument.Value)}', which it walks", false);
            foreach (var read in reads)
            {
                if (read.PerElement && LoadsLazily(source, elementType, read.Path))
                {
                    var full = SourceTracer.Join(source.Path, read.Path);
                    AddLazyLoad((invocation, invocation.TargetMethod, full), Operations.ReportLocation(invocation), source.Root, Messages.LazyLoadPerElement(full, iteration, callee));
                }
            }
        }
    }

    /// <summary>
    /// A member of the entity itself, called or read on an element (<c>shipment.Describe()</c>,
    /// <c>shipment.ParcelWeight</c>), that reads navigations of <c>this</c> lazy-loads them once per element.
    /// </summary>
    /// <param name="operation">The member access, for the iteration and deduplication.</param>
    /// <param name="instance">The instance the member is called on.</param>
    /// <param name="member">The method or getter.</param>
    /// <param name="location">Where to report.</param>
    private void CheckInstanceMember(IOperation operation, IOperation? instance, IMethodSymbol member, Location location)
    {
        if (instance is null || member.IsStatic || _lazy.ElementOf(instance) is not { } element || LazyLoadingTurnedOffBefore(operation))
        {
            return;
        }

        var source = _tracer.Trace(instance);
        if (Operations.StripConversions(instance)?.Type is not { } entityType)
        {
            return;
        }

        foreach (var path in ReadsOnThis(member, entityType))
        {
            if (LoadsLazily(source, entityType, path))
            {
                var full = SourceTracer.Join(source.Path, path);
                AddLazyLoad((element.Iteration.Region, element.Root, full), location, source.Root, Messages.LazyLoadPerElement(full, element.Iteration, Model.Symbols.Display(member)));
            }
        }
    }

    /// <summary>
    /// A call through a delegate local, field or auto-property (<c>await load(id)</c>, <c>_count(id)</c>) inside an
    /// iteration runs what was assigned to it once per element: its lambdas' round trips, its method groups', or the
    /// query of a compiled query (<c>EF.CompileQuery</c>).
    /// </summary>
    /// <param name="context">The operation context.</param>
    /// <param name="invocation">The delegate invocation.</param>
    private void CheckStoredDelegate(OperationAnalysisContext context, IInvocationOperation invocation)
    {
        var callee = Operations.StripConversions(invocation.Instance) switch
        {
            ILocalReferenceOperation local => local.Local.Name,
            IFieldReferenceOperation field => field.Field.Name,
            IPropertyReferenceOperation property => property.Property.Name,
            _ => null,
        };
        if (callee is null || _iterations.Find(invocation) is not { Kind: not IterationKind.ExpressionTree } iteration)
        {
            return;
        }

        var arguments = TraceArguments(invocation);
        var (lambdas, methods, compiled) = session.CallbacksOf(invocation.Instance);
        foreach (var kind in new[] { EffectKind.Query, EffectKind.Save })
        {
            Witness? witness = null;
            foreach (var lambda in lambdas)
            {
                witness ??= session.FindWitnessInBody(lambda.Body, lambda.Symbol, arguments, kind);
            }

            foreach (var target in methods)
            {
                witness ??= session.FindWitness(target, arguments, kind);
            }

            foreach (var compile in compiled)
            {
                if (kind == EffectKind.Query && witness is null)
                {
                    var effect = StoredCallbacks.CompiledQueryEffect(compile);
                    witness = new Witness(EffectKind.Query, effect.Name, ImmutableArray<string>.Empty, effect.Shape, effect.Location);
                }
            }

            if (witness is null || Exemptions.IsIntended(invocation, iteration, witness.Shape))
            {
                continue;
            }

            // The lambda has no name of its own: the chain starts at the local it is called through.
            if (lambdas.Count > 0 && witness.Chain.Length > 0)
            {
                witness = witness with { Chain = witness.Chain.SetItem(0, callee) };
            }
            var location = Operations.ReportLocation(invocation);
            context.ReportDiagnostic(kind == EffectKind.Query
                ? Diagnostic.Create(Descriptors.QueryPerElement, location, AdditionalLocations(witness), Messages.QueryPerElement(witness, iteration, callee))
                : Diagnostic.Create(Descriptors.SavePerElement, location, AdditionalLocations(witness), Messages.SavePerElement(witness, iteration, callee)));
        }
    }

    /// <summary>
    /// A call that serializes its argument (<c>JsonSerializer.Serialize</c>, <c>JsonConvert.SerializeObject</c>,
    /// SignalR's <c>SendAsync</c> and the methods of a strongly typed hub client) reads every navigation of the
    /// entities in it, like an endpoint's result.
    /// </summary>
    /// <param name="context">The operation context.</param>
    /// <param name="invocation">The call.</param>
    private void CheckSerializerCall(OperationAnalysisContext context, IInvocationOperation invocation)
    {
        var method = invocation.TargetMethod;
        if (IsTypedHubClientCall(invocation))
        {
            foreach (var argument in invocation.Arguments)
            {
                CheckSerializedEntities(context, argument.Value, argument.Value, "This call serializes", JsonSerializerKind.SystemTextJson);
            }

            return;
        }

        var type = method.ContainingType is null ? "" : Model.Symbols.FullName(method.ContainingType);
        JsonSerializerKind? serializer = type switch
        {
            "System.Text.Json.JsonSerializer" when method.Name.StartsWith("Serialize", StringComparison.Ordinal) => JsonSerializerKind.SystemTextJson,
            "Newtonsoft.Json.JsonConvert" when method.Name == "SerializeObject" => JsonSerializerKind.Newtonsoft,
            _ when type.StartsWith("Microsoft.AspNetCore.SignalR.", StringComparison.Ordinal) && method.Name is "SendAsync" or "SendCoreAsync" => JsonSerializerKind.SystemTextJson,
            _ => null,
        };
        if (serializer is null)
        {
            return;
        }

        foreach (var argument in invocation.Arguments)
        {
            // JsonSerializer and JsonConvert take "value"; SignalR's SendAsync takes arg1..arg10.
            if (argument.Parameter?.Name is "value" || argument.Parameter?.Name.StartsWith("arg", StringComparison.Ordinal) == true)
            {
                CheckSerializedEntities(context, argument.Value, argument.Value, "This call serializes", serializer.Value);
            }
        }
    }

    /// <summary>
    /// Whether a call goes to a strongly typed SignalR client (<c>Clients.All.OrdersChanged(orders)</c> on a
    /// <c>Hub&lt;IOrderClient&gt;</c> or an <c>IHubContext&lt;THub, IOrderClient&gt;</c>): an interface method called on
    /// what SignalR's <c>IHubClients&lt;T&gt;</c> hands out (<c>All</c>, <c>Caller</c>, <c>Group(name)</c>, ...), whose
    /// arguments SignalR serializes to the clients.
    /// </summary>
    /// <param name="invocation">The call.</param>
    private static bool IsTypedHubClientCall(IInvocationOperation invocation)
    {
        if (invocation.TargetMethod.ContainingType is not { TypeKind: TypeKind.Interface })
        {
            return false;
        }

        var provider = Operations.StripConversions(invocation.Instance) switch
        {
            IPropertyReferenceOperation property => property.Property.ContainingType,
            IInvocationOperation call => call.TargetMethod.ContainingType,
            _ => null,
        };
        return provider is { IsGenericType: true }
               && provider.ContainingNamespace?.ToDisplayString() == "Microsoft.AspNetCore.SignalR"
               && provider.Name is "IHubClients" or "IHubCallerClients";
    }

    /// <summary>
    /// A method group passed to an iterating method (<c>ids.Select(Load)</c>) is called once per
    /// element: reports the round trips and lazy loads it makes.
    /// </summary>
    /// <param name="context">The operation context.</param>
    /// <param name="invocation">The call to the iterating method.</param>
    private void CheckMethodGroups(OperationAnalysisContext context, IInvocationOperation invocation)
    {
        foreach (var argument in invocation.Arguments)
        {
            if (Operations.AsMethodGroup(argument.Value) is not { } group || !session.IsIterating(invocation.TargetMethod, argument.Parameter))
            {
                continue;
            }

            var iteration = new Iteration(IterationKind.Callback, argument.Value, $"the '{invocation.TargetMethod.Name}' call", false);
            var sequence = _tracer.SequenceArgument(invocation);
            var element = sequence is null ? SourceInfo.Unknown : _tracer.Trace(sequence).Element();
            var elementArguments = ImmutableArray.Create(element);
            var callee = Model.Symbols.Display(group);
            var location = argument.Value.Syntax.GetLocation();

            // orders.Select(mapper.Map<OrderView>): the mapper maps each element of the sequence.
            if (Mappings.LibraryOf(group) is { } library && group.TypeArguments.Length > 0
                && session.Symbols.ElementType(sequence?.Type) is { } elementType)
            {
                var mapped = new MappedElements(elementType, element, group.TypeArguments[group.TypeArguments.Length - 1], library, iteration, location, (argument.Value, group));
                CheckMapping(context, argument.Value, mapped);
                continue;
            }

            if (session.FindWitness(group, elementArguments, EffectKind.Query) is { } query)
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.QueryPerElement, location, AdditionalLocations(query), Messages.QueryPerElement(query, iteration, callee)));
            }

            if (session.FindWitness(group, elementArguments, EffectKind.Save) is { } save)
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.SavePerElement, location, AdditionalLocations(save), Messages.SavePerElement(save, iteration, callee)));
            }

            if (group.Parameters.Length == 0 || LazyLoadingTurnedOffBefore(invocation))
            {
                continue;
            }

            foreach (var read in session.NavigationReadsOf(group, 0))
            {
                var path = read.Path;
                if (!read.PerElement && LoadsLazily(element, group.Parameters[0].Type, path))
                {
                    var full = SourceTracer.Join(element.Path, path);
                    AddLazyLoad((argument.Value, group, full), location, element.Root, Messages.LazyLoadPerElement(full, iteration, callee));
                }
            }
        }
    }

    /// <summary>
    /// An element passed to a method that reads its lazy navigations (a mapper) lazy-loads them once
    /// per element.
    /// </summary>
    /// <param name="invocation">The call inside an iteration.</param>
    private void CheckMapperArguments(IInvocationOperation invocation)
    {
        foreach (var argument in invocation.Arguments)
        {
            if (argument.Parameter is not null)
            {
                CheckElementArgument(invocation, argument.Value, invocation.TargetMethod, argument.Parameter.Ordinal);
            }
        }
    }

    /// <summary>NP0004: a direct recursive call from a method that runs a query on every call.</summary>
    /// <param name="context">The operation context.</param>
    /// <param name="invocation">The call, which is not inside an iteration.</param>
    /// <param name="arguments">The traced arguments.</param>
    private void CheckRecursion(OperationAnalysisContext context, IInvocationOperation invocation, ImmutableArray<SourceInfo> arguments)
    {
        if (owner is not IMethodSymbol self)
        {
            return;
        }

        var callee = invocation.TargetMethod.OriginalDefinition;
        if (SymbolEqualityComparer.Default.Equals(callee, self.OriginalDefinition))
        {
            if (session.FindWitness(self, arguments, EffectKind.Query) is { } witness)
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.RecursiveQuery, Operations.ReportLocation(invocation), Messages.RecursiveQuery(Model.Symbols.Display(self), witness, null)));
            }

            return;
        }

        // Recursion through another method (A calls B, B queries and calls A): reported once, at the
        // call made by the method that runs the query itself. A non-virtual call (base.Get(id) in the override of
        // Get) runs only the callee's own body, never the overrides that dispatch would reach.
        var depth = session.Settings.MaxCallDepth;
        if (DirectQuery(self) is { } own
            && (invocation.IsVirtual ? session.Reaches(callee, self, depth) : session.ReachesFromBody(callee, self, depth)))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.RecursiveQuery, Operations.ReportLocation(invocation), Messages.RecursiveQuery(Model.Symbols.Display(self), own, Model.Symbols.Display(callee))));
        }
    }

    /// <summary>A query the method runs in its own body (not through calls), as a witness.</summary>
    /// <param name="method">The method.</param>
    private Witness? DirectQuery(IMethodSymbol method)
    {
        foreach (var effect in session.Summary(method.OriginalDefinition).Effects)
        {
            if (effect is { Kind: EffectKind.Query, Source.Kind: OriginKind.Ef })
            {
                return new Witness(EffectKind.Query, effect.Name, ImmutableArray.Create(Model.Symbols.Display(method)), effect.Shape, effect.Location);
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a call cannot be followed and looks like data access: no body in the compilation,
    /// a type matching <c>nplusone.data_access_types</c>, and a result (a method that returns nothing,
    /// or a bare <c>Task</c>, is a command, not a lookup). ABP's own repository methods are known
    /// (<see cref="AbpCalls"/>): one that is not a round trip, such as <c>InsertAsync</c> without
    /// <c>autoSave</c>, is not a guess either.
    /// </summary>
    /// <param name="method">The method called.</param>
    private bool IsOpaqueDataAccess(IMethodSymbol method)
    {
        if (method.ReturnsVoid
            || SymbolEqualityComparer.Default.Equals(method.ReturnType, session.Symbols.Task)
            || method.ContainingType is null
            || Model.Symbols.FullName(method.ContainingType).StartsWith("System.", StringComparison.Ordinal)
            || Model.Symbols.FullName(method.ContainingType).StartsWith("Microsoft.", StringComparison.Ordinal)
            || (session.Symbols.IsAbpRepository(method.ContainingType) && method.OriginalDefinition.DeclaringSyntaxReferences.IsEmpty))
        {
            return false;
        }

        return session.IsOpaque(method)
               && !session.Settings.IgnoredMethods.MatchesMethod(method.OriginalDefinition)
               && session.Settings.DataAccessTypes.MatchesType(method.ContainingType);
    }

    /// <summary>Traces the arguments of a call by the callee's parameter ordinals.</summary>
    /// <param name="invocation">The call.</param>
    private ImmutableArray<SourceInfo> TraceArguments(IInvocationOperation invocation)
    {
        var builder = ImmutableArray.CreateBuilder<SourceInfo>(invocation.TargetMethod.Parameters.Length);
        for (var i = 0; i < invocation.TargetMethod.Parameters.Length; i++)
        {
            builder.Add(SourceInfo.Unknown);
        }

        foreach (var argument in invocation.Arguments)
        {
            if (argument.Parameter is { } parameter && parameter.Ordinal < builder.Count
                && parameter.Type.SpecialType == SpecialType.None && parameter.Type.TypeKind != TypeKind.Struct && parameter.Type.TypeKind != TypeKind.Enum)
            {
                builder[parameter.Ordinal] = _tracer.Trace(argument.Value);
            }
        }

        return builder.MoveToImmutable();
    }

    /// <summary>Checks the values a minimal API handler lambda returns.</summary>
    /// <param name="context">The operation context.</param>
    /// <param name="invocation">The <c>MapGet</c>/<c>MapPost</c>/... call.</param>
    private void CheckMinimalApiHandler(OperationAnalysisContext context, IInvocationOperation invocation)
    {
        foreach (var argument in invocation.Arguments)
        {
            if (Operations.AsLambda(argument.Value) is not { } handler)
            {
                continue;
            }

            foreach (var operation in handler.Body.Descendants())
            {
                if (operation is IReturnOperation { ReturnedValue: { } value } returned && NearestFunction(returned) == handler)
                {
                    CheckEndpointResult(context, value);
                }
            }
        }
    }

    /// <summary>
    /// NP0005: the value an endpoint returns, once unwrapped from <c>await</c>, <c>Ok(...)</c> and
    /// <c>TypedResults.Ok(...)</c>, holds entities with lazy navigations the query did not include. ASP.NET Core
    /// serializes with System.Text.Json, or with Newtonsoft.Json for MVC actions when the application calls
    /// <c>AddNewtonsoftJson</c>; each honours only its own ignore attributes.
    /// </summary>
    /// <param name="context">The operation context.</param>
    /// <param name="returned">The returned expression.</param>
    private void CheckEndpointResult(OperationAnalysisContext context, IOperation returned)
        => CheckSerializedEntities(context, returned, UnwrapResult(returned), "The endpoint returns", EndpointSerializer);

    /// <summary>The serializer that writes the owner's results: Newtonsoft.Json for MVC actions of an application that switched to it.</summary>
    private JsonSerializerKind EndpointSerializer
        => session.MvcUsesNewtonsoft && IsAction(owner, session.Symbols) ? JsonSerializerKind.Newtonsoft : JsonSerializerKind.SystemTextJson;

    /// <summary>
    /// NP0005 for any value a serializer walks: it holds entities with lazy navigations the query did
    /// not load, so serializing it loads them one element at a time.
    /// </summary>
    /// <param name="context">The operation context.</param>
    /// <param name="reported">Where to report.</param>
    /// <param name="value">The serialized value.</param>
    /// <param name="subject">How the message starts: what hands the value to the serializer.</param>
    /// <param name="serializer">The serializer that walks the value; navigations it ignores are never read.</param>
    private void CheckSerializedEntities(OperationAnalysisContext context, IOperation reported, IOperation value, string subject, JsonSerializerKind serializer)
    {
        if (session.Entities.AnyLazyLoading)
        {
            CheckSerializedValue(context, new SerializedSite(reported, subject, serializer), value, "", 0);
        }
    }

    /// <summary>
    /// Checks one serialized value: entities, or a result container built in place (a page, an anonymous object)
    /// whose serialized members hold entities. The navigation paths of entities inside a container start with the
    /// members leading to them (<c>Data.Customer</c>). Containers are followed three levels deep.
    /// </summary>
    /// <param name="context">The operation context.</param>
    /// <param name="site">Where the value is serialized.</param>
    /// <param name="value">The value.</param>
    /// <param name="member">The container members leading to the value, empty for the serialized value itself.</param>
    /// <param name="depth">How many containers deep the value is.</param>
    private void CheckSerializedValue(OperationAnalysisContext context, SerializedSite site, IOperation value, string member, int depth)
    {
        if (Operations.StripConversions(value) is not { } unwrapped)
        {
            return;
        }

        // A value passed as object (SignalR's arg1) is converted; its own type is what gets serialized.
        value = unwrapped;
        var type = session.Symbols.Unwrap(value.Type);
        var element = session.Symbols.ElementType(type);
        var entity = element ?? type;
        if (!session.Entities.IsEntity(entity))
        {
            if (depth < 3)
            {
                foreach (var (name, held) in SerializedMembers.Of(value, site.Serializer))
                {
                    CheckSerializedValue(context, site, held, SourceTracer.Join(member, name), depth + 1);
                }
            }

            return;
        }

        var source = _tracer.Trace(value);
        if (source.Kind != OriginKind.Ef)
        {
            return;
        }

        var uncovered = UncoveredNavigations(entity!, source, single: element is null && !site.Item, site.Serializer);
        if (uncovered.Count > 0)
        {
            var paths = uncovered.ConvertAll(path => SourceTracer.Join(member, path));
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.SerializedLazyEntities, site.Reported.Syntax.GetLocation(), Messages.SerializedLazyEntities(site.Subject, entity!.Name, paths)));
        }
    }

    /// <summary>
    /// The lazy navigation paths, two levels deep, that serializing the result loads once per
    /// element. For a single entity only the elements of its collections count: loading one
    /// collection is one query, loading a navigation of each of its elements is N. A reference back to
    /// the entity a collection was loaded from (<c>Order.Customer</c> under <c>Customer.Orders</c>) is fixed up by
    /// EF Core and costs nothing; a collection back (<c>Customer.Orders</c> under <c>Order.Customer</c>) is not
    /// marked loaded and is queried once per element. A navigation the serializer ignores (<c>[JsonIgnore]</c>) is
    /// never read, and neither is anything below it.
    /// </summary>
    /// <param name="entity">The returned entity type.</param>
    /// <param name="source">The returned value's source.</param>
    /// <param name="single">Whether one entity, not a collection, is returned.</param>
    /// <param name="serializer">The serializer that walks the entities.</param>
    private List<string> UncoveredNavigations(ITypeSymbol entity, SourceInfo source, bool single, JsonSerializerKind serializer)
    {
        var uncovered = new List<string>();
        foreach (var first in session.Entities.NavigationsOf(entity))
        {
            if (!LoadsLazily(first, source) || JsonIgnores.Skips(first.Property, serializer))
            {
                continue;
            }

            var firstPath = SourceTracer.Join(source.Path, first.Property.Name);
            if (!single && !source.Loads(firstPath, first))
            {
                uncovered.Add(first.Property.Name);
            }

            if (single && !first.IsCollection)
            {
                continue;
            }

            foreach (var second in session.Entities.NavigationsOf(first.Target))
            {
                if (!LoadsLazily(second, source) || JsonIgnores.Skips(second.Property, serializer)
                    || !second.IsCollection && SymbolEqualityComparer.Default.Equals(second.Target, entity.OriginalDefinition))
                {
                    continue;
                }

                if (!source.Loads(SourceTracer.Join(firstPath, second.Property.Name), second))
                {
                    uncovered.Add(first.Property.Name + "." + second.Property.Name);
                }
            }
        }

        return uncovered;
    }

    /// <summary>
    /// Whether reading <paramref name="relativePath"/> on an element runs a query: the element comes
    /// from a tracked EF query, and some navigation on the path is lazy and was neither included nor
    /// auto-included.
    /// </summary>
    /// <param name="element">The element's source.</param>
    /// <param name="elementType">The element's type.</param>
    /// <param name="relativePath">The navigation path read on the element.</param>
    private bool LoadsLazily(SourceInfo element, ITypeSymbol elementType, string relativePath)
    {
        if (element.Kind != OriginKind.Ef || relativePath.Length == 0)
        {
            return false;
        }

        var type = elementType;
        var path = element.Path;
        foreach (var segment in relativePath.Split('.'))
        {
            var navigation = session.Entities.NavigationsOf(type).FirstOrDefault(n => n.Property.Name == segment);
            if (navigation is null)
            {
                return false;
            }

            path = SourceTracer.Join(path, segment);
            if (LoadsLazily(navigation, element) && !element.Loads(path, navigation))
            {
                return true;
            }

            type = navigation.Target;
        }

        return false;
    }

    /// <summary>Whether reading the navigation on an entity from that source loads it: lazy, and loadable when the query does not track.</summary>
    /// <param name="navigation">The navigation.</param>
    /// <param name="source">Where the entity comes from.</param>
    private static bool LoadsLazily(Navigation navigation, SourceInfo source)
        => source.Untracked ? navigation.IsLazyWhenUntracked : navigation.IsLazy;

    /// <summary>
    /// Whether the navigation was loaded explicitly on the same entity earlier in the method:
    /// <c>db.Entry(o).Reference(x =&gt; x.Customer).LoadAsync()</c> (or <c>Collection</c>), or ABP's
    /// <c>repository.EnsurePropertyLoadedAsync(o, x =&gt; x.Customer)</c> (or <c>EnsureCollectionLoadedAsync</c>). Reading it
    /// afterwards loads nothing; the explicit load is the round trip, and NP0001 reports it. A filtered load
    /// (<c>Collection(...).Query().Where(...).Load()</c>) does not mark the collection loaded, so it does not count.
    /// </summary>
    /// <param name="read">The navigation read.</param>
    /// <param name="entity">The expression of the entity the navigation is read on.</param>
    /// <param name="navigation">The name of the navigation read on it.</param>
    private static bool LoadedExplicitlyBefore(IOperation read, IOperation? entity, string navigation)
    {
        if (ReferencedSymbol(entity) is not { } target)
        {
            return false;
        }

        var position = read.Syntax.SpanStart;
        foreach (var descendant in Operations.Root(read).Descendants())
        {
            if (descendant is not IInvocationOperation invocation || invocation.Syntax.SpanStart >= position)
            {
                continue;
            }

            var (loaded, selector) = invocation.TargetMethod.Name switch
            {
                "Load" or "LoadAsync" when invocation.Instance is IInvocationOperation { TargetMethod.Name: "Reference" or "Collection", Arguments.Length: 1 } member
                                           && member.Instance is IInvocationOperation { TargetMethod.Name: "Entry", Arguments.Length: 1 } entry
                    => (entry.Arguments[0].Value, member.Arguments[0].Value),
                "EnsurePropertyLoadedAsync" or "EnsureCollectionLoadedAsync"
                    => (invocation.Arguments.FirstOrDefault(static a => a.Parameter?.Name == "entity")?.Value,
                        invocation.Arguments.FirstOrDefault(static a => a.Parameter?.Name == "propertyExpression")?.Value),
                _ => (null, null),
            };
            if (loaded is not null && selector is not null
                && SymbolEqualityComparer.Default.Equals(ReferencedSymbol(loaded), target)
                && QueryCalls.SelectorPath(selector) == navigation)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The local or parameter an expression reads, or null.</summary>
    /// <param name="operation">The expression.</param>
    private static ISymbol? ReferencedSymbol(IOperation? operation) => Operations.StripConversions(operation) switch
    {
        ILocalReferenceOperation local => local.Local,
        IParameterReferenceOperation parameter => parameter.Parameter,
        _ => null,
    };

    /// <summary>
    /// Whether the method turns lazy loading off (<c>ChangeTracker.LazyLoadingEnabled = false</c>) before
    /// <paramref name="operation"/>; reads after it load nothing.
    /// </summary>
    /// <param name="operation">A navigation read or a call that leads to one.</param>
    private static bool LazyLoadingTurnedOffBefore(IOperation operation)
    {
        var position = operation.Syntax.SpanStart;
        foreach (var descendant in Operations.Root(operation).Descendants())
        {
            if (descendant is ISimpleAssignmentOperation { Target: IPropertyReferenceOperation { Property.Name: "LazyLoadingEnabled" }, Value.ConstantValue: { HasValue: true, Value: false } } assignment
                && assignment.Syntax.SpanStart < position)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Removes conversions and result wrappers (<c>Ok</c>, <c>Json</c>, <c>TypedResults.Ok</c>,
    /// <c>new OkObjectResult(...)</c>, <c>new JsonResult(...)</c>); an <c>await</c> stays, its type is the result's.
    /// </summary>
    /// <param name="operation">The returned expression.</param>
    private IOperation UnwrapResult(IOperation operation)
    {
        var current = operation;
        while (true)
        {
            current = Operations.StripConversions(current)!;
            if (current is IInvocationOperation { Arguments.Length: > 0 } wrapper && IsResultWrapper(wrapper.TargetMethod))
            {
                // Ok, Created, ... take "value"; Json and Results.Json take "data", followed by optional arguments.
                var argument = wrapper.Arguments.FirstOrDefault(static a => a.Parameter?.Name is "value" or "data") ?? wrapper.Arguments[wrapper.Arguments.Length - 1];
                current = argument.Value;
                continue;
            }

            if (current is IObjectCreationOperation { Type: { } created } creation && IsSerializingResult(created)
                && creation.Arguments.FirstOrDefault(a => a.Parameter?.Name == "value") is { } valueArgument)
            {
                current = valueArgument.Value;
                continue;
            }

            return current;
        }
    }

    /// <summary>
    /// Whether a method wraps a response value that gets serialized: <c>ControllerBase.Ok</c>,
    /// <c>Results.Json</c>, <c>TypedResults.Ok</c>, ... A view (<c>View</c>, <c>PartialView</c>,
    /// <c>ViewComponent</c>) is rendered, not serialized: it reads what its markup reads.
    /// </summary>
    /// <param name="method">The method.</param>
    private bool IsResultWrapper(IMethodSymbol method)
    {
        var type = method.ContainingType;
        if (method.ReturnType.Name is "ViewResult" or "PartialViewResult" or "ViewComponentResult")
        {
            return false;
        }

        return KnownSymbols.InheritsFrom(type, session.Symbols.ControllerBase)
               || SymbolEqualityComparer.Default.Equals(type, session.Symbols.Results)
               || SymbolEqualityComparer.Default.Equals(type, session.Symbols.TypedResults);
    }

    /// <summary>Whether a result type serializes the value it is created with: an <c>ObjectResult</c> or a <c>JsonResult</c>.</summary>
    /// <param name="type">The created type.</param>
    private bool IsSerializingResult(ITypeSymbol type)
        => KnownSymbols.InheritsFrom(type, session.Symbols.ObjectResult)
           || KnownSymbols.InheritsFrom(type, session.Symbols.JsonResult);

    /// <summary>Whether a call maps a minimal API endpoint (<c>MapGet</c>, <c>MapPost</c>, ...).</summary>
    /// <param name="method">The method.</param>
    internal static bool IsEndpointMapping(IMethodSymbol method)
        => method.Name.StartsWith("Map", StringComparison.Ordinal)
           && method.ContainingType?.Name == "EndpointRouteBuilderExtensions";

    /// <summary>
    /// Whether a symbol is a SignalR hub method a client can call: a public instance method of a
    /// <c>Hub</c> (or <c>Hub&lt;T&gt;</c>). SignalR serializes what it returns to the caller.
    /// </summary>
    /// <param name="symbol">The block owner.</param>
    /// <param name="symbols">The known symbols.</param>
    private static bool IsHubMethod(ISymbol symbol, KnownSymbols symbols)
        => symbol is IMethodSymbol { MethodKind: MethodKind.Ordinary, IsStatic: false, DeclaredAccessibility: Accessibility.Public } method
           && method.ContainingType is { TypeKind: TypeKind.Class, IsAbstract: false } type
           && KnownSymbols.InheritsFrom(type, symbols.Hub);

    /// <summary>Whether a return statement belongs to the owner itself rather than a lambda or local function in it.</summary>
    /// <param name="returned">The return.</param>
    private static bool BelongsToOwner(IReturnOperation returned) => NearestFunction(returned) is null;

    /// <summary>The innermost lambda or local function around an operation, or null.</summary>
    /// <param name="operation">The operation.</param>
    private static IOperation? NearestFunction(IOperation operation)
    {
        for (var current = operation.Parent; current is not null; current = current.Parent)
        {
            if (current is IAnonymousFunctionOperation or ILocalFunctionOperation)
            {
                return current;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether an action is an OData query endpoint (<c>[EnableQuery]</c> on it or its controller): OData's formatter writes
    /// only the structural properties and what <c>$expand</c> asks for, which goes into the SQL query, so the serializer
    /// never lazy-loads.
    /// </summary>
    /// <param name="symbol">The block owner.</param>
    private static bool HasEnableQuery(ISymbol symbol)
        => HasEnableQueryAttribute(symbol) || symbol.ContainingType is { } controller && HasEnableQueryAttribute(controller);

    /// <summary>Whether a symbol carries OData's <c>[EnableQuery]</c>.</summary>
    /// <param name="symbol">The action or its controller.</param>
    private static bool HasEnableQueryAttribute(ISymbol symbol)
    {
        foreach (var attribute in symbol.GetAttributes())
        {
            if (attribute.AttributeClass is { Name: "EnableQueryAttribute" } enableQuery
                && enableQuery.ContainingNamespace?.ToDisplayString() == "Microsoft.AspNetCore.OData.Query")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a symbol is an MVC action: a public instance method of a controller that is not <c>[NonAction]</c>.</summary>
    /// <param name="symbol">The block owner.</param>
    /// <param name="symbols">The known symbols.</param>
    private static bool IsAction(ISymbol symbol, KnownSymbols symbols)
    {
        if (symbol is not IMethodSymbol { MethodKind: MethodKind.Ordinary, IsStatic: false, DeclaredAccessibility: Accessibility.Public } method
            || method.ContainingType is not { TypeKind: TypeKind.Class, IsAbstract: false } type)
        {
            return false;
        }

        var isController = KnownSymbols.InheritsFrom(type, symbols.ControllerBase)
                           || type.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, symbols.ApiControllerAttribute));
        return isController && !method.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, symbols.NonActionAttribute));
    }

    /// <summary>
    /// Records a lazy load, keeping the earliest one per iteration, element and path. The query's
    /// root goes along as an additional location and the path as a property, so the code fix can add
    /// the <c>Include</c>.
    /// </summary>
    /// <param name="key">The deduplication key.</param>
    /// <param name="location">Where to report it.</param>
    /// <param name="queryRoot">Where the query that produced the elements starts, when known.</param>
    /// <param name="message">The message.</param>
    private void AddLazyLoad((IOperation Region, ISymbol Root, string Path) key, Location location, Location? queryRoot, string message)
    {
        lock (_gate)
        {
            if (_lazyLoads.TryGetValue(key, out var existing) && existing.Location.SourceSpan.Start <= location.SourceSpan.Start)
            {
                return;
            }

            var properties = ImmutableDictionary<string, string?>.Empty.Add(DiagnosticProperties.IncludePath, key.Path);
            _lazyLoads[key] = Diagnostic.Create(Descriptors.LazyLoadPerElement, location, queryRoot is null ? null : [queryRoot], properties, message);
        }
    }

    /// <summary>Whether a call reads a local declared inside a loop (a context created per element): it cannot move out of it.</summary>
    /// <param name="call">The call.</param>
    /// <param name="loop">The loop.</param>
    private static bool UsesLocalsOf(IOperation call, IOperation loop)
    {
        foreach (var operation in call.DescendantsAndSelf())
        {
            if (operation is ILocalReferenceOperation reference
                && reference.Local.Locations.FirstOrDefault() is { IsInSource: true } location
                && loop.Syntax.Span.Contains(location.SourceSpan))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The location of the round trip a witness found, shown as an additional location.</summary>
    /// <param name="witness">The witness.</param>
    private static IEnumerable<Location>? AdditionalLocations(Witness witness)
        => witness.Location is null ? null : [witness.Location];
}
