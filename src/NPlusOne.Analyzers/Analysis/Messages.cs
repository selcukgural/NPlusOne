using System.Text;

namespace NPlusOne.Analyzers.Analysis;

/// <summary>
/// The text of every diagnostic. A message names what runs, where it repeats and how it is reached,
/// then suggests the rewrite that fits the query's shape.
/// </summary>
internal static class Messages
{
    /// <summary>NP0001: a query once per element.</summary>
    /// <param name="witness">The query and the chain that reaches it.</param>
    /// <param name="iteration">The loop or callback.</param>
    /// <param name="callee">The method called at the reported site, or null when the query runs right there.</param>
    /// <param name="recursive">Whether the call is the method calling itself, which walks a tree one node at a time.</param>
    public static string QueryPerElement(Witness witness, Iteration iteration, string? callee, bool recursive = false)
    {
        var what = (callee, witness.Operator) switch
        {
            (null, "foreach") => "Enumerating this query runs it",
            (null, _) => $"'{witness.Operator}' runs a query",
            _ => $"'{callee}' runs a query{Via(witness, callee)}",
        };
        var advice = recursive
            ? "The recursion walks the tree one node at a time; load all rows in one query and link them in memory, or use a recursive common table expression."
            : Suggest(witness.Shape);
        return $"{what} once per element of {iteration.Description}. {advice}";
    }

    /// <summary>NP0002: a lazy navigation read once per element.</summary>
    /// <param name="path">The navigation path from the query's root entity.</param>
    /// <param name="iteration">The loop or callback.</param>
    /// <param name="reader">The mapper that reads it, or null when it is read at the reported site.</param>
    public static string LazyLoadPerElement(string path, Iteration iteration, string? reader)
    {
        var who = reader is null ? "" : $" by '{reader}'";
        return $"'{path}' is lazy-loaded{who} once per element of {iteration.Description}, because the query that produced the elements does not include it. "
               + $"Add {IncludeCall(path)} to that query, or project the needed columns with Select.";
    }

    /// <summary>NP0003: <c>SaveChanges</c> once per element.</summary>
    /// <param name="witness">The save and the chain that reaches it.</param>
    /// <param name="iteration">The loop or callback.</param>
    /// <param name="callee">The method called at the reported site, or null when it saves right there.</param>
    public static string SavePerElement(Witness witness, Iteration iteration, string? callee)
    {
        if (witness.Operator.StartsWith("BeginTransaction", StringComparison.Ordinal))
        {
            var starts = callee is null ? $"'{witness.Operator}'" : $"'{callee}' starts a transaction{Via(witness, callee)} and";
            return $"{starts} runs once per element of {iteration.Description}: a begin and a commit per element, and each element commits on its own. "
                   + "Start one transaction around the loop and commit it once, with SaveChanges after the loop.";
        }

        var what = callee is null ? $"'{witness.Operator}'" : $"'{callee}' calls SaveChanges{Via(witness, callee)} and";
        return $"{what} runs once per element of {iteration.Description}: one round trip and one transaction per element. "
               + "Call SaveChanges once after the loop, or update all rows with one ExecuteUpdate or ExecuteDelete.";
    }

    /// <summary>NP0004: a recursive method that queries on every call.</summary>
    /// <param name="method">The recursive method.</param>
    /// <param name="witness">The query it runs.</param>
    /// <param name="through">The method it recurses through, or null when it calls itself directly.</param>
    public static string RecursiveQuery(string method, Witness witness, string? through)
    {
        var how = through is null ? "calls itself" : $"calls itself through '{through}'";
        return $"'{method}' {how} and runs a query ('{witness.Operator}') on every call, so a chain or tree of depth N costs N round trips. "
               + "Load all the rows in one query and link them in memory, or use a recursive common table expression.";
    }

    /// <summary>NP0005: an endpoint or a serializer call handed lazily loaded entities.</summary>
    /// <param name="subject">What hands them to the serializer: <c>The endpoint returns</c>, <c>This call serializes</c>.</param>
    /// <param name="entity">The entity type name.</param>
    /// <param name="paths">Navigation paths serializing will load one element at a time.</param>
    public static string SerializedLazyEntities(string subject, string entity, IReadOnlyList<string> paths)
    {
        var shown = paths.Count <= 3 ? string.Join(", ", paths) : string.Join(", ", paths.Take(3)) + ", ...";
        return $"{subject} '{entity}' entities whose lazy navigations are not included ({shown}); serializing them loads those navigations one element at a time. "
               + "Serialize DTOs projected with Select, or Include everything that gets serialized.";
    }

    /// <summary>NP0006: a data-access call whose body is not visible, once per element.</summary>
    /// <param name="callee">The method called.</param>
    /// <param name="iteration">The loop or callback.</param>
    public static string PossibleQueryPerElement(string callee, Iteration iteration)
        => $"'{callee}' looks like data access and is called once per element of {iteration.Description}, but its implementation is not visible to the analyzer. "
           + "Add a method that loads the data for all elements at once, or declare it in nplusone.query_methods (or nplusone.ignored_methods if it does not query).";

    /// <summary>
    /// The chain part of a message: <c> ('ToListAsync' in OrderRepository.Get, via Service.Load)</c>.
    /// The called method itself is not repeated: a query in its own body shows as <c> ('ToListAsync')</c>.
    /// </summary>
    /// <param name="witness">The witness.</param>
    /// <param name="callee">The method called at the reported site.</param>
    private static string Via(Witness witness, string callee)
    {
        var chain = witness.Chain.Length > 0 && witness.Chain[0] == callee ? witness.Chain.RemoveAt(0) : witness.Chain;
        if (chain.IsEmpty)
        {
            return witness.Operator == callee.Substring(callee.LastIndexOf('.') + 1) ? "" : $" ('{witness.Operator}')";
        }

        var where = chain[chain.Length - 1];
        var through = chain.Length > 1 ? ", via " + string.Join(" → ", chain.Take(chain.Length - 1)) : "";
        return $" ('{witness.Operator}' in {where}{through})";
    }

    /// <summary>NP0007: a mapper's naming conventions read lazy navigations per element.</summary>
    /// <param name="source">The source entity type name.</param>
    /// <param name="destination">The destination type name.</param>
    /// <param name="paths">The navigations it reads that the query did not load.</param>
    /// <param name="iteration">The loop, callback or mapped sequence.</param>
    public static string ConventionMappingPerElement(string source, string destination, IReadOnlyList<string> paths, Iteration iteration)
        => $"By the mapper's naming conventions, mapping '{source}' to '{destination}' reads '{string.Join("', '", paths)}' once per element of {iteration.Description}, "
           + $"and the query did not include it; with lazy loading on, that is one query per element. Add {IncludeCall(paths[0])} to the query, "
           + "or let the query project the destination (ProjectTo, ProjectToType or Select). The mapping configuration is not visible here, so this is based on the conventions alone.";

    /// <summary>The rewrite that removes the per-element round trips, by query shape.</summary>
    /// <param name="shape">The query's shape.</param>
    private static string Suggest(QueryShape shape)
    {
        var (p, member) = (shape.KeyParameter ?? "x", shape.KeyMember);
        var aggregate = AggregateName(shape.Operator);
        return shape.Kind switch
        {
            ShapeKind.Find =>
                "Load them all up front with one query, Where(e => ids.Contains(e.Id)), and look them up with ToDictionary(e => e.Id).",
            ShapeKind.Aggregate when member is not null && aggregate is "Any" or "All" or "Contains" =>
                $"Find the matching keys for all elements in one query up front, Where({p} => keys.Contains({p}.{member})).Select({p} => {p}.{member}).Distinct(), and test each element against that set.",
            ShapeKind.Aggregate when member is not null =>
                $"Compute it for all elements in one query up front: GroupBy({p} => {p}.{member}).Select(g => new {{ g.Key, Value = g.{aggregate}({shape.Selector}) }}).",
            ShapeKind.Rows when member is not null =>
                $"Load the rows for all elements up front with one query, Where({p} => keys.Contains({p}.{member})), and group them with ToLookup({p} => {p}.{member}).",
            ShapeKind.ExplicitLoad =>
                "Include the navigation in the query that loaded these entities, or load it for all of them with one query.",
            ShapeKind.Statement =>
                "Run one set-based statement for all elements instead: ExecuteUpdate or ExecuteDelete over Where(x => keys.Contains(x.Key)).",
            ShapeKind.Declared =>
                "Add a method that loads the data for all elements at once and call it before iterating.",
            _ =>
                "Load the data for all elements up front in one query (filter with Contains, then use ToLookup or ToDictionary), or fold it into the query that produced the elements.",
        };
    }

    /// <summary>The synchronous name of an aggregate operator: <c>CountAsync</c> gives <c>Count</c>.</summary>
    /// <param name="name">The operator name.</param>
    private static string AggregateName(string name)
        => name.EndsWith("Async", StringComparison.Ordinal) ? name.Substring(0, name.Length - 5) : name;

    /// <summary>The include call for a path: <c>Lines.Product</c> gives <c>.Include(x =&gt; x.Lines).ThenInclude(x =&gt; x.Product)</c>.</summary>
    /// <param name="path">The navigation path.</param>
    private static string IncludeCall(string path)
    {
        var segments = path.Split('.');
        var builder = new StringBuilder();
        for (var i = 0; i < segments.Length; i++)
        {
            builder.Append(i == 0 ? ".Include(x => x." : ".ThenInclude(x => x.").Append(segments[i]).Append(')');
        }

        return builder.ToString();
    }
}
