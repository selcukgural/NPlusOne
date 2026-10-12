using DbEye.Common.Options;
using DbEye.Core.Collector;
using DbEye.Core.Interceptor;
using DbEye.Core.Middleware;
using EFCore.QueryGuard;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NPlusOne.EntityFrameworkCore;
using StackExchange.Profiling;
using StackExchange.Profiling.Data;
using StackExchange.Profiling.Internal;

namespace NPlusOne.Analyzers.Tests.Harness.GroundTruth;

/// <summary>A runtime N+1 detector the corpus can run under: one fresh <see cref="IDetectorRun"/> per run of a method.</summary>
public interface IRuntimeDetector
{
    /// <summary>A short name, safe in an assembly name.</summary>
    string Key { get; }

    IDetectorRun NewRun();
}

/// <summary>One run under a detector: attach it to every context, start it before the method, read what it reported after.</summary>
public interface IDetectorRun
{
    void Configure(DbContextOptionsBuilder builder);

    void Begin();

    IReadOnlyList<string> Finish();
}

/// <summary>This repository's runtime guard, writes included.</summary>
public sealed class NPlusOneGuardDetector : IRuntimeDetector
{
    public string Key => "NPlusOneGuard";

    public IDetectorRun NewRun() => new Run();

    private sealed class Run : IDetectorRun
    {
        private NPlusOneScope? _scope;

        public void Configure(DbContextOptionsBuilder builder) => builder.UseNPlusOneGuard();

        public void Begin() => _scope = NPlusOneGuard.Begin(o => o.IncludeWrites = true);

        public IReadOnlyList<string> Finish()
        {
            using var scope = _scope!;
            return [.. scope.Report.Suspects.Select(s => $"{s.Kind} x{s.Count} at {s.CallSite}: {s.Navigation ?? s.Sql}")];
        }
    }
}

/// <summary>EFCore.QueryGuard with its defaults: its interceptor on every context, one of its scopes per run, its N+1 violations.</summary>
public sealed class QueryGuardDetector : IRuntimeDetector
{
    public string Key => "QueryGuard";

    public IDetectorRun NewRun() => new Run();

    private sealed class Run : IDetectorRun
    {
        private readonly QueryGuardInterceptor _interceptor = new(new QueryGuardOptions(), NullLogger<QueryGuardInterceptor>.Instance);

        public void Configure(DbContextOptionsBuilder builder) => builder.AddInterceptors(_interceptor);

        public void Begin() => _interceptor.BeginScope();

        public IReadOnlyList<string> Finish()
            => [.. _interceptor.EndScope().Where(v => v.Type == ViolationType.NPlusOne).Select(v => $"x{v.RepeatCount}: {v.Sql}")];
    }
}

/// <summary>
/// DbEye with its defaults: its interceptor collects into a request's collector, and its own middleware, run after the method
/// as at the end of a request, decides and logs "N+1 detected".
/// </summary>
public sealed class DbEyeDetector : IRuntimeDetector
{
    public string Key => "DbEye";

    public IDetectorRun NewRun() => new Run();

    private sealed class Run : IDetectorRun
    {
        private readonly HttpContextAccessor _accessor = new();
        private readonly ServiceProvider _requestServices = new ServiceCollection().AddScoped<DbEyeCollector>().BuildServiceProvider();
        private readonly DbEyeInterceptor _interceptor;
        private IServiceScope? _request;

        public Run() => _interceptor = new DbEyeInterceptor(_accessor);

        public void Configure(DbContextOptionsBuilder builder) => builder.AddInterceptors(_interceptor);

        public void Begin()
        {
            _request = _requestServices.CreateScope();
            _accessor.HttpContext = new DefaultHttpContext { RequestServices = _request.ServiceProvider };
            _accessor.HttpContext.Request.Path = "/case";
        }

        public IReadOnlyList<string> Finish()
        {
            var logs = new CapturingLogger<DbEyeMiddleware>();
            var middleware = new DbEyeMiddleware(_ => Task.CompletedTask, logs, new Development());
            middleware.InvokeAsync(_accessor.HttpContext!, Microsoft.Extensions.Options.Options.Create(new DbEyeOptions())).GetAwaiter().GetResult();
            _accessor.HttpContext = null;
            _request?.Dispose();
            _requestServices.Dispose();
            return
            [
                .. logs.Messages.Where(m => m.Contains("N+1 detected", StringComparison.Ordinal))
                    .Select(m => m.Split('\n').First(l => l.Contains("repeated", StringComparison.Ordinal)).Trim()),
            ];
        }
    }

    private sealed class Development : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";

        public string ApplicationName { get; set; } = "Corpus";

        public string WebRootPath { get; set; } = "";

        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();

        public string ContentRootPath { get; set; } = "";

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

/// <summary>
/// MiniProfiler with its EF Core listener: one profiler per run; a SQL command whose text repeats in one profile is what its
/// UI marks as a duplicate.
/// </summary>
public sealed class MiniProfilerDetector : IRuntimeDetector
{
    private static readonly Lazy<bool> Listening = new(() =>
    {
        new DiagnosticInitializer([new RelationalDiagnosticListener()]).Start();
        return true;
    });

    public string Key => "MiniProfiler";

    public IDetectorRun NewRun() => new Run();

    private sealed class Run : IDetectorRun
    {
        private MiniProfiler? _profiler;

        public void Configure(DbContextOptionsBuilder builder) => _ = Listening.Value;

        public void Begin() => _profiler = MiniProfiler.StartNew("case");

        public IReadOnlyList<string> Finish()
        {
            _profiler!.Stop();
            var ignored = _profiler.Options.IgnoredDuplicateExecuteTypes;
            return
            [
                .. _profiler.GetTimingHierarchy()
                    .SelectMany(t => t.CustomTimings?.Values.SelectMany(v => v) ?? [])
                    .Where(c => !ignored.Contains(c.ExecuteType ?? ""))
                    .GroupBy(c => c.CommandString)
                    .Where(g => g.Count() > 1)
                    .Select(g => $"x{g.Count()}: {g.Key}"),
            ];
        }
    }
}

/// <summary>A logger that keeps the messages written to it.</summary>
public sealed class CapturingLogger<T> : ILogger<T>
{
    public List<string> Messages { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Messages.Add(formatter(state, exception));
}
