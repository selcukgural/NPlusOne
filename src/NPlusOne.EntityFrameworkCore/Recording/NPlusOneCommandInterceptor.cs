using System.Runtime.CompilerServices;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace NPlusOne.EntityFrameworkCore.Recording;

/// <summary>
/// Records every command EF Core is about to execute into the current scope. Stateless: one shared
/// instance serves every DbContext, which also keeps EF Core's internal service provider cache stable.
/// </summary>
/// <remarks>
/// The executing hooks are used (not <c>CommandInitialized</c>) because they fire once per command
/// that really runs. Outside a scope each hook is one async-local read and returns. One
/// <c>SaveChanges</c> call is one write however many statements it sends (EF Core batches them, and they
/// commit together), so only its first statement is recorded; a transaction the code starts itself is
/// a write too, the one <c>SaveChanges</c> opens is not.
/// </remarks>
internal sealed class NPlusOneCommandInterceptor : DbCommandInterceptor, ISaveChangesInterceptor, IDbTransactionInterceptor
{
    /// <summary>
    /// The <c>SaveChanges</c> call in progress per context. Keyed by the context, not an async-local: EF Core runs several
    /// interceptors through an async composite, and a value set there does not flow back. A context runs one operation at a time.
    /// </summary>
    private static readonly ConditionalWeakTable<DbContext, SaveInProgress> Saving = [];

    /// <summary>The shared instance added by <c>UseNPlusOneGuard</c>.</summary>
    public static NPlusOneCommandInterceptor Instance { get; } = new();

    /// <summary>Marks the start of a <c>SaveChanges</c> call.</summary>
    /// <param name="eventData">The context saving.</param>
    /// <param name="result">Passed through unchanged.</param>
    /// <returns><paramref name="result"/>.</returns>
    public InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        BeginSave(eventData.Context);
        return result;
    }

    /// <summary>Marks the start of a <c>SaveChangesAsync</c> call.</summary>
    /// <param name="eventData">The context saving.</param>
    /// <param name="result">Passed through unchanged.</param>
    /// <param name="cancellationToken">Not used.</param>
    /// <returns><paramref name="result"/>.</returns>
    public ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        BeginSave(eventData.Context);
        return ValueTask.FromResult(result);
    }

    /// <summary>Marks the end of a <c>SaveChanges</c> call.</summary>
    /// <param name="eventData">The context that saved.</param>
    /// <param name="result">Passed through unchanged.</param>
    /// <returns><paramref name="result"/>.</returns>
    public int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        EndSave(eventData.Context);
        return result;
    }

    /// <summary>Marks the end of a <c>SaveChangesAsync</c> call.</summary>
    /// <param name="eventData">The context that saved.</param>
    /// <param name="result">Passed through unchanged.</param>
    /// <param name="cancellationToken">Not used.</param>
    /// <returns><paramref name="result"/>.</returns>
    public ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        EndSave(eventData.Context);
        return ValueTask.FromResult(result);
    }

    /// <summary>Marks the end of a failed <c>SaveChanges</c> call.</summary>
    /// <param name="eventData">The context that failed to save.</param>
    public void SaveChangesFailed(DbContextErrorEventData eventData) => EndSave(eventData.Context);

    /// <summary>Marks the end of a failed <c>SaveChangesAsync</c> call.</summary>
    /// <param name="eventData">The context that failed to save.</param>
    /// <param name="cancellationToken">Not used.</param>
    /// <returns>A completed task.</returns>
    public Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        EndSave(eventData.Context);
        return Task.CompletedTask;
    }

    /// <summary>Marks the end of a cancelled <c>SaveChanges</c> call.</summary>
    /// <param name="eventData">The context whose save was cancelled.</param>
    public void SaveChangesCanceled(DbContextEventData eventData) => EndSave(eventData.Context);

    /// <summary>Marks the end of a cancelled <c>SaveChangesAsync</c> call.</summary>
    /// <param name="eventData">The context whose save was cancelled.</param>
    /// <param name="cancellationToken">Not used.</param>
    /// <returns>A completed task.</returns>
    public Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken cancellationToken = default)
    {
        EndSave(eventData.Context);
        return Task.CompletedTask;
    }

    /// <summary>Records a transaction the code starts (<c>Database.BeginTransaction</c>) as a write.</summary>
    /// <param name="connection">Not used.</param>
    /// <param name="eventData">The context starting it.</param>
    /// <param name="result">Passed through unchanged.</param>
    /// <returns><paramref name="result"/>.</returns>
    public InterceptionResult<DbTransaction> TransactionStarting(DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result)
    {
        RecordTransaction(eventData.Context);
        return result;
    }

    /// <summary>Records a transaction the code starts asynchronously as a write.</summary>
    /// <param name="connection">Not used.</param>
    /// <param name="eventData">The context starting it.</param>
    /// <param name="result">Passed through unchanged.</param>
    /// <param name="cancellationToken">Not used.</param>
    /// <returns><paramref name="result"/>.</returns>
    public ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
        DbConnection connection,
        TransactionStartingEventData eventData,
        InterceptionResult<DbTransaction> result,
        CancellationToken cancellationToken = default)
    {
        RecordTransaction(eventData.Context);
        return ValueTask.FromResult(result);
    }

    /// <summary>Records a reader command (queries, and <c>SaveChanges</c> batches that return generated values).</summary>
    /// <param name="command">The command.</param>
    /// <param name="eventData">EF Core's data about the command.</param>
    /// <param name="result">Passed through unchanged.</param>
    /// <returns><paramref name="result"/>.</returns>
    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Record(command, eventData);
        return result;
    }

    /// <summary>Records a reader command executed asynchronously.</summary>
    /// <param name="command">The command.</param>
    /// <param name="eventData">EF Core's data about the command.</param>
    /// <param name="result">Passed through unchanged.</param>
    /// <param name="cancellationToken">Not used.</param>
    /// <returns><paramref name="result"/>.</returns>
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Record(command, eventData);
        return ValueTask.FromResult(result);
    }

    /// <summary>Records a scalar command.</summary>
    /// <param name="command">The command.</param>
    /// <param name="eventData">EF Core's data about the command.</param>
    /// <param name="result">Passed through unchanged.</param>
    /// <returns><paramref name="result"/>.</returns>
    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Record(command, eventData);
        return result;
    }

    /// <summary>Records a scalar command executed asynchronously.</summary>
    /// <param name="command">The command.</param>
    /// <param name="eventData">EF Core's data about the command.</param>
    /// <param name="result">Passed through unchanged.</param>
    /// <param name="cancellationToken">Not used.</param>
    /// <returns><paramref name="result"/>.</returns>
    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        Record(command, eventData);
        return ValueTask.FromResult(result);
    }

    /// <summary>Records a non-query command.</summary>
    /// <param name="command">The command.</param>
    /// <param name="eventData">EF Core's data about the command.</param>
    /// <param name="result">Passed through unchanged.</param>
    /// <returns><paramref name="result"/>.</returns>
    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Record(command, eventData);
        return result;
    }

    /// <summary>Records a non-query command executed asynchronously.</summary>
    /// <param name="command">The command.</param>
    /// <param name="eventData">EF Core's data about the command.</param>
    /// <param name="result">Passed through unchanged.</param>
    /// <param name="cancellationToken">Not used.</param>
    /// <returns><paramref name="result"/>.</returns>
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Record(command, eventData);
        return ValueTask.FromResult(result);
    }

    /// <summary>
    /// Classifies a command as a read or a write. EF Core's command source decides when it is
    /// specific; unknown sources fall back to the first keyword of the SQL. Migrations and value
    /// generators (sequence round trips) are infrastructure and never recorded.
    /// </summary>
    /// <param name="source">Where EF Core says the command comes from.</param>
    /// <param name="sql">The command text.</param>
    /// <returns>The kind, or <see langword="null"/> when the command is not recorded.</returns>
    internal static NPlusOneSuspectKind? Classify(CommandSource source, string sql) => source switch
    {
        CommandSource.LinqQuery or CommandSource.FromSqlQuery => NPlusOneSuspectKind.Query,
        CommandSource.SaveChanges or CommandSource.ExecuteUpdate or CommandSource.ExecuteDelete or CommandSource.ExecuteSqlRaw => NPlusOneSuspectKind.Write,
        CommandSource.Migrations or CommandSource.ValueGenerator => null,
        _ => StartsWithReadKeyword(sql) ? NPlusOneSuspectKind.Query : NPlusOneSuspectKind.Write,
    };

    /// <summary>
    /// Records the command into the current scope, if any. The guard observes; it never fails the application's
    /// command: anything that goes wrong while recording (a user pattern timing out, say) drops that one record.
    /// </summary>
    /// <param name="command">The command.</param>
    /// <param name="eventData">EF Core's data about the command.</param>
    private static void Record(DbCommand command, CommandEventData eventData)
    {
        var scope = NPlusOneScope.Current;
        if (scope is null)
        {
            return;
        }

        try
        {
            var sql = command.CommandText ?? string.Empty;
            if (Classify(eventData.CommandSource, sql) is not { } kind)
            {
                return;
            }

            // The statements after the first one of a SaveChanges call belong to the same write.
            if (eventData.CommandSource == CommandSource.SaveChanges && eventData.Context is { } context
                && Saving.TryGetValue(context, out var save) && Interlocked.Exchange(ref save.Recorded, 1) == 1)
            {
                return;
            }

            NPlusOneScope.RecordCommand(scope, kind, sql, SqlFingerprint.DialectOf(eventData.Context?.Database.ProviderName));
        }
#pragma warning disable CA1031 // Recording must never break the command it observes.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    /// <summary>Notes that a context began a <c>SaveChanges</c> call; nothing to note while no scope records.</summary>
    /// <param name="context">The context, when EF Core gives it.</param>
    private static void BeginSave(DbContext? context)
    {
        if (context is not null && NPlusOneScope.Current is not null)
        {
            Saving.AddOrUpdate(context, new SaveInProgress());
        }
    }

    /// <summary>Notes that a context's <c>SaveChanges</c> call ended.</summary>
    /// <param name="context">The context, when EF Core gives it.</param>
    private static void EndSave(DbContext? context)
    {
        if (context is not null)
        {
            Saving.Remove(context);
        }
    }

    /// <summary>Records a transaction start into the current scope, unless <c>SaveChanges</c> opened it.</summary>
    /// <param name="context">The context starting the transaction.</param>
    private static void RecordTransaction(DbContext? context)
    {
        if (NPlusOneScope.Current is not { } scope || context is not null && Saving.TryGetValue(context, out _))
        {
            return;
        }

        try
        {
            NPlusOneScope.RecordCommand(scope, NPlusOneSuspectKind.Write, "BEGIN TRANSACTION", SqlDialect.Unknown);
        }
#pragma warning disable CA1031 // Recording must never break the transaction it observes.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    /// <summary>A <c>SaveChanges</c> call in progress: whether its first statement has been recorded.</summary>
    private sealed class SaveInProgress
    {
        /// <summary>1 once the call's first statement is recorded.</summary>
        public int Recorded;
    }

    /// <summary>Whether the SQL starts with <c>SELECT</c> or <c>WITH</c> after leading whitespace.</summary>
    /// <param name="sql">The command text.</param>
    /// <returns><see langword="true"/> for reads.</returns>
    private static bool StartsWithReadKeyword(string sql)
    {
        var text = sql.AsSpan().TrimStart();
        return text.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) || text.StartsWith("WITH", StringComparison.OrdinalIgnoreCase);
    }
}
