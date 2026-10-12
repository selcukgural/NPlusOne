using System.Net;
using Microsoft.Extensions.Logging;
using NPlusOne.EntityFrameworkCore;

namespace NPlusOne.AspNetCore.Tests;

public sealed class MiddlewareTests
{
    [Fact]
    public async Task Request_with_a_loop_logs_a_warning_with_the_report()
    {
        await using var app = await TestApp.StartAsync();

        var response = await app.Client.GetAsync("/loop");

        response.EnsureSuccessStatusCode();
        var entry = Assert.Single(app.Logs.Guard);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("NPlusOneSuspects", entry.EventName);
        Assert.StartsWith("GET HTTP: GET /loop", entry.Message, StringComparison.Ordinal);
        Assert.Contains("Query ran 5 times at ShopContext.CountOrdersOneByOneAsync (ShopModel.cs:", entry.Message, StringComparison.Ordinal);
    }

    // A WebSocket keeps one request open for a whole session; one scope would add up separate actions into one N+1.
    [Fact]
    public async Task WebSocket_requests_are_not_watched()
    {
        await using var app = await TestApp.StartAsync();

        using var socket = await app.WebSockets.ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None);
        var result = await socket.ReceiveAsync(new ArraySegment<byte>(new byte[16]), CancellationToken.None);
        await socket.CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        await app.WebSocketDone.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(System.Net.WebSockets.WebSocketMessageType.Close, result.MessageType);
        Assert.Empty(app.Logs.Guard);
    }

    [Fact]
    public async Task Clean_request_logs_nothing()
    {
        await using var app = await TestApp.StartAsync();

        var response = await app.Client.GetAsync("/clean");

        Assert.Equal("10", await response.Content.ReadAsStringAsync());
        Assert.Empty(app.Logs.Guard);
    }

    [Fact]
    public async Task Each_request_has_its_own_scope()
    {
        await using var app = await TestApp.StartAsync();

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.NoContent, (await app.Client.GetAsync("/twice")).StatusCode);
        }

        Assert.Empty(app.Logs.Guard);
    }

    [Fact]
    public async Task ThrowOnViolation_throws_when_the_response_has_not_started()
    {
        await using var app = await TestApp.StartAsync(o => o.ThrowOnViolation = true);

        var exception = await Assert.ThrowsAsync<NPlusOneViolationException>(() => app.Client.GetAsync("/loop-no-body"));

        Assert.Equal(5, Assert.Single(exception.Report!.Suspects).Count);
        Assert.Empty(app.Logs.Guard);
    }

    [Fact]
    public async Task ThrowOnViolation_logs_an_error_when_the_response_has_started()
    {
        await using var app = await TestApp.StartAsync(o => o.ThrowOnViolation = true);

        var response = await app.Client.GetAsync("/loop");

        Assert.Equal("10", await response.Content.ReadAsStringAsync());
        var entry = Assert.Single(app.Logs.Guard);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal("NPlusOneViolationAfterResponseStarted", entry.EventName);
        Assert.Contains("Query ran 5 times", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Configured_options_apply_to_the_request_scope()
    {
        await using var app = await TestApp.StartAsync(o => o.RepeatThreshold = 10);

        (await app.Client.GetAsync("/loop")).EnsureSuccessStatusCode();

        Assert.Empty(app.Logs.Guard);
    }

    [Fact]
    public async Task Mvc_actions_are_covered_by_the_middleware()
    {
        await using var app = await TestApp.StartAsync();

        (await app.Client.GetAsync("/mvc/unchecked-loop")).EnsureSuccessStatusCode();

        var entry = Assert.Single(app.Logs.Guard);
        Assert.Contains("ShopController.UncheckedLoop", entry.Message, StringComparison.Ordinal);
    }
}
