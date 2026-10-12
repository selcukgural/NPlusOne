using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace NPlusOne.Analyzers.Tests.Harness.GroundTruth;

/// <summary>
/// Records every SQL command EF Core sends to the database while recording is on: the ground truth a
/// corpus case is measured by. The statements of one <c>SaveChanges</c> count as one entry: SQLite runs
/// them one by one, while SQL Server and PostgreSQL batch them into one round trip. A transaction the code starts
/// itself counts too (its begin and commit are round trips on a database server); the one SaveChanges opens does not.
/// </summary>
public sealed class CommandLog : DbCommandInterceptor, ISaveChangesInterceptor, IDbTransactionInterceptor
{
    private readonly List<string> _commands = [];
    private int _saving;
    private int _savedStatements;

    /// <summary>Whether commands are recorded (off while the schema is created and the data seeded).</summary>
    public bool Recording { get; set; }

    /// <summary>The recorded command texts, in order.</summary>
    public IReadOnlyList<string> Commands => _commands;

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Record(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Record(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Record(command);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Record(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Record(command);
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        Record(command);
        return ValueTask.FromResult(result);
    }

    public InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        BeginSave();
        return result;
    }

    public ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        BeginSave();
        return ValueTask.FromResult(result);
    }

    public int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        EndSave();
        return result;
    }

    public ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        EndSave();
        return ValueTask.FromResult(result);
    }

    public void SaveChangesFailed(DbContextErrorEventData eventData) => EndSave();

    public Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        EndSave();
        return Task.CompletedTask;
    }

    public InterceptionResult<DbTransaction> TransactionStarting(DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result)
    {
        RecordTransaction();
        return result;
    }

    public ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
    {
        RecordTransaction();
        return ValueTask.FromResult(result);
    }

    private void RecordTransaction()
    {
        if (!Recording || _saving > 0)
        {
            return;
        }

        lock (_commands)
        {
            _commands.Add("BEGIN TRANSACTION");
        }
    }

    private void BeginSave()
    {
        if (Interlocked.Increment(ref _saving) == 1)
        {
            _savedStatements = 0;
        }
    }

    private void EndSave()
    {
        if (Interlocked.Decrement(ref _saving) != 0 || _savedStatements == 0 || !Recording)
        {
            return;
        }

        lock (_commands)
        {
            _commands.Add($"SaveChanges ({_savedStatements} statements)");
        }
    }

    private void Record(DbCommand command)
    {
        if (!Recording)
        {
            return;
        }

        if (_saving > 0)
        {
            _savedStatements++;
            return;
        }

        lock (_commands)
        {
            _commands.Add(command.CommandText);
        }
    }
}
