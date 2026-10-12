using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NPlusOne.EntityFrameworkCore;

namespace NPlusOne.AspNetCore.Tests;

/// A real WebApplication on TestServer with the guard middleware, MVC and minimal API endpoints,
/// over a private in-memory SQLite database with 5 customers.
public sealed class TestApp : IAsyncDisposable
{
    public const int CustomerCount = 5;

    private readonly SqliteConnection _keepAlive;
    private readonly WebApplication _app;

    private TestApp(SqliteConnection keepAlive, WebApplication app, CapturedLogs logs)
    {
        _keepAlive = keepAlive;
        _app = app;
        Logs = logs;
        Client = app.GetTestClient();
    }

    public HttpClient Client { get; }

    public WebSocketClient WebSockets => _app.GetTestServer().CreateWebSocketClient();

    public CapturedLogs Logs { get; }

    public static async Task<TestApp> StartAsync(Action<NPlusOneOptions>? configure = null, bool useMiddleware = true)
    {
        var connectionString = $"Data Source=aspnet-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        var keepAlive = new SqliteConnection(connectionString);
        keepAlive.Open();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing",
            ApplicationName = typeof(TestApp).Assembly.GetName().Name,
        });
        builder.WebHost.UseTestServer();
        var logs = new CapturedLogs();
        builder.Logging.ClearProviders().AddProvider(logs);

        builder.Services.AddNPlusOneGuard(configure);
        builder.Services.AddDbContext<ShopContext>(o => o.UseSqlite(connectionString).UseNPlusOneGuard());
        builder.Services.AddControllers().AddApplicationPart(typeof(TestApp).Assembly);

        var app = builder.Build();
        app.UseWebSockets();
        var webSocketDone = new TaskCompletionSource();

        // Outside the guard: completes once the guard middleware has finished with the WebSocket request.
        app.Use(async (context, next) =>
        {
            await next(context);
            if (context.Request.Path == "/ws")
            {
                webSocketDone.TrySetResult();
            }
        });
        if (useMiddleware)
        {
            app.UseNPlusOneGuard();
        }

        // A long-lived connection that runs a loop of queries, like a hub method called over a WebSocket.
        app.Map("/ws", async (HttpContext context, ShopContext db) =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await db.CountOrdersOneByOneAsync();
            await socket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        });

        app.MapControllers();
        app.MapGet("/loop", (ShopContext db) => db.CountOrdersOneByOneAsync());
        app.MapGet("/loop-no-body", async (ShopContext db) =>
        {
            await db.CountOrdersOneByOneAsync();
            return Results.NoContent();
        });
        app.MapGet("/clean", (ShopContext db) => db.CountOrdersAtOnceAsync());
        app.MapGet("/twice", async (ShopContext db) =>
        {
            for (var i = 0; i < 2; i++)
            {
                await db.Orders.CountAsync(o => o.CustomerId == i);
            }

            return Results.NoContent();
        });
        var asserted = app.MapGroup("/asserted").WithNPlusOneAssert();
        asserted.MapGet("/loop", (ShopContext db) => db.CountOrdersOneByOneAsync());
        asserted.MapGet("/clean", (ShopContext db) => db.CountOrdersAtOnceAsync());

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShopContext>();
            await db.Database.EnsureCreatedAsync();
            for (var c = 1; c <= CustomerCount; c++)
            {
                db.Customers.Add(new Customer { Name = $"Customer {c}", Orders = [new Order(), new Order()] });
            }

            await db.SaveChangesAsync();
        }

        await app.StartAsync();
        return new TestApp(keepAlive, app, logs) { WebSocketDone = webSocketDone.Task };
    }

    /// Completes when a request to /ws has gone back out through the guard middleware.
    public Task WebSocketDone { get; private init; } = Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.DisposeAsync();
        await _keepAlive.DisposeAsync();
    }
}
