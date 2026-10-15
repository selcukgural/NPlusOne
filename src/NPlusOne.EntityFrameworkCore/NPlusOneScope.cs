using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using NPlusOne.EntityFrameworkCore.Recording;

namespace NPlusOne.EntityFrameworkCore;

/// <summary>
/// A period (a test, a request, any block of code) during which the guard records the commands EF
/// Core runs and the navigations it lazy-loads. Created by <see cref="NPlusOneGuard.Begin()"/>;
/// dispose it to stop recording and make the previous scope current again.
/// </summary>
/// <remarks>
/// The current scope flows with the async context (<see cref="AsyncLocal{T}"/>), so parallel tests or
/// requests each see their own scope. Scopes nest: while an inner scope is active, work is recorded
/// in the inner scope and in every enclosing scope, each with its own settings. Recording is
/// thread-safe, because EF Core may run commands on several contexts at once in one async flow.
/// </remarks>
public sealed class NPlusOneScope : IDisposable
{
    /// <summary>The innermost scope of the current async flow, or <see langword="null"/> outside any scope.</summary>
    private static readonly AsyncLocal<NPlusOneScope?> CurrentScope = new();

    /// <summary>Run counts per group. Counters are boxed so they can be incremented with <see cref="Interlocked"/>.</summary>
    private readonly ConcurrentDictionary<GroupKey, StrongBox<int>> _groups = new();

    /// <summary>The settings copied from the options when the scope began.</summary>
    private readonly ScopeSettings _settings;

    /// <summary>The scope that was current when this one began; restored on dispose and also recorded into.</summary>
    private readonly NPlusOneScope? _parent;

    /// <summary>Number of recorded commands; updated with <see cref="Interlocked"/>.</summary>
    private int _commandCount;

    /// <summary>Number of recorded lazy loads; updated with <see cref="Interlocked"/>.</summary>
    private int _lazyLoadCount;

    /// <summary>1 once disposed; a disposed scope records nothing even if an async flow still points at it.</summary>
    private int _disposed;

    /// <summary>Creates a scope; it becomes current through <see cref="Start"/>.</summary>
    /// <param name="settings">The settings snapshot.</param>
    /// <param name="parent">The scope that was current before.</param>
    private NPlusOneScope(ScopeSettings settings, NPlusOneScope? parent)
    {
        _settings = settings;
        _parent = parent;
    }

    /// <summary>The innermost active scope of the current async flow, or <see langword="null"/>.</summary>
    internal static NPlusOneScope? Current => CurrentScope.Value;

    /// <summary>
    /// What the scope has recorded so far. Each read builds a fresh snapshot; after the scope is
    /// disposed the report stays available and no longer changes.
    /// </summary>
    public NPlusOneReport Report => BuildReport();

    /// <summary>
    /// Throws <see cref="NPlusOneViolationException"/> when the scope has suspects. The exception
    /// message is the readable report. Can be called before or after the scope is disposed.
    /// </summary>
    /// <exception cref="NPlusOneViolationException">The scope has at least one suspect.</exception>
    public void AssertNoNPlusOne()
    {
        var report = BuildReport();
        if (report.HasSuspects)
        {
            throw new NPlusOneViolationException(report);
        }
    }

    /// <summary>
    /// Stops recording and makes the nearest enclosing scope that is still active current again. Safe to call more than
    /// once, and in any order: an outer scope disposed before an inner one is skipped when the inner one ends.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        if (ReferenceEquals(CurrentScope.Value, this))
        {
            var parent = _parent;
            while (parent is not null && parent.IsDisposed)
            {
                parent = parent._parent;
            }

            CurrentScope.Value = parent;
        }
    }

    /// <summary>Creates a scope and makes it the current one for this async flow.</summary>
    /// <param name="settings">The settings snapshot for the new scope.</param>
    /// <returns>The new, current scope.</returns>
    internal static NPlusOneScope Start(ScopeSettings settings)
    {
        var scope = new NPlusOneScope(settings, CurrentScope.Value);
        CurrentScope.Value = scope;
        return scope;
    }

    /// <summary>
    /// Records a command in <paramref name="innermost"/> and every enclosing scope that accepts it.
    /// The fingerprint and the call site are computed at most once, and only if a scope needs them.
    /// </summary>
    /// <param name="innermost">The current scope.</param>
    /// <param name="kind"><see cref="NPlusOneSuspectKind.Query"/> or <see cref="NPlusOneSuspectKind.Write"/>.</param>
    /// <param name="sql">The command text as sent to the database; never stored.</param>
    /// <param name="dialect">The dialect the command is written in, which decides how its literals are found.</param>
    internal static void RecordCommand(NPlusOneScope innermost, NPlusOneSuspectKind kind, string sql, SqlDialect dialect)
    {
        string? fingerprint = null;
        string? callSite = null;
        var callSiteCaptured = false;
        for (var scope = innermost; scope is not null; scope = scope._parent)
        {
            if (scope.IsDisposed || !scope._settings.Accepts(kind, sql))
            {
                continue;
            }

            fingerprint ??= SqlFingerprint.Create(sql, dialect);
            if (scope._settings.CaptureCallSite && !callSiteCaptured)
            {
                callSite = CallSiteLocator.Capture(null);
                callSiteCaptured = true;
            }

            scope.Increment(new GroupKey(kind, fingerprint, scope._settings.CaptureCallSite ? callSite : null));
            Interlocked.Increment(ref scope._commandCount);
        }
    }

    /// <summary>Records a lazy load in <paramref name="innermost"/> and every enclosing scope.</summary>
    /// <param name="innermost">The current scope.</param>
    /// <param name="navigation">The navigation as <c>Entity.Navigation</c>.</param>
    /// <param name="entityType">The entity CLR type, whose property getters are skipped when looking for the call site.</param>
    internal static void RecordLazyLoad(NPlusOneScope innermost, string navigation, Type entityType)
    {
        string? callSite = null;
        var callSiteCaptured = false;
        for (var scope = innermost; scope is not null; scope = scope._parent)
        {
            if (scope.IsDisposed)
            {
                continue;
            }

            if (scope._settings.CaptureCallSite && !callSiteCaptured)
            {
                callSite = CallSiteLocator.Capture(entityType);
                callSiteCaptured = true;
            }

            scope.Increment(new GroupKey(NPlusOneSuspectKind.LazyLoad, navigation, scope._settings.CaptureCallSite ? callSite : null));
            Interlocked.Increment(ref scope._lazyLoadCount);
        }
    }

    /// <summary>Whether the scope has been disposed.</summary>
    private bool IsDisposed => Volatile.Read(ref _disposed) == 1;

    /// <summary>Adds one run to a group, creating the group on first use.</summary>
    /// <param name="key">The group.</param>
    private void Increment(GroupKey key)
    {
        if (IsDisposed)
        {
            return;
        }

        var counter = _groups.GetOrAdd(key, static _ => new StrongBox<int>());
        Interlocked.Increment(ref counter.Value);
    }

    /// <summary>Builds a report from the current counts; suspects are sorted most frequent first.</summary>
    /// <returns>The report.</returns>
    private NPlusOneReport BuildReport()
    {
        List<NPlusOneSuspect> suspects = [];
        foreach (var (key, counter) in _groups)
        {
            var count = Volatile.Read(ref counter.Value);
            if (count <= _settings.RepeatThreshold)
            {
                continue;
            }

            suspects.Add(key.Kind == NPlusOneSuspectKind.LazyLoad
                ? new NPlusOneSuspect(key.Kind, count, null, key.Text, key.CallSite)
                : new NPlusOneSuspect(key.Kind, count, Preview(key.Text), null, key.CallSite));
        }

        suspects.Sort(CompareSuspects);
        return new NPlusOneReport(suspects, Volatile.Read(ref _commandCount), Volatile.Read(ref _lazyLoadCount), _settings.RepeatThreshold);
    }

    /// <summary>Cuts a fingerprint to the configured preview length.</summary>
    /// <param name="fingerprint">The normalized SQL.</param>
    /// <returns>The fingerprint, or its start followed by "...".</returns>
    private string Preview(string fingerprint) =>
        fingerprint.Length <= _settings.MaxSqlPreviewLength
            ? fingerprint
            : string.Concat(fingerprint.AsSpan(0, _settings.MaxSqlPreviewLength), "...");

    /// <summary>Orders suspects by count (descending), then kind, text and call site, so reports are stable.</summary>
    /// <param name="left">The first suspect.</param>
    /// <param name="right">The second suspect.</param>
    /// <returns>The sort order.</returns>
    private static int CompareSuspects(NPlusOneSuspect left, NPlusOneSuspect right)
    {
        var order = right.Count.CompareTo(left.Count);
        if (order != 0)
        {
            return order;
        }

        order = left.Kind.CompareTo(right.Kind);
        if (order != 0)
        {
            return order;
        }

        order = string.CompareOrdinal(left.Navigation ?? left.Sql, right.Navigation ?? right.Sql);
        return order != 0 ? order : string.CompareOrdinal(left.CallSite, right.CallSite);
    }
}
