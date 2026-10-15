using NPlusOne.EntityFrameworkCore;

namespace NPlusOne.AspNetCore.Tests;

public sealed class AssertFilterTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Mvc_attribute_fails_an_action_with_a_loop(bool useMiddleware)
    {
        await using var app = await TestApp.StartAsync(useMiddleware: useMiddleware);

        var exception = await Assert.ThrowsAsync<NPlusOneViolationException>(() => app.Client.GetAsync("/mvc/loop"));

        Assert.Contains("Query ran 5 times at ShopContext.CountOrdersOneByOneAsync", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mvc_attribute_passes_a_clean_action()
    {
        await using var app = await TestApp.StartAsync();

        var response = await app.Client.GetAsync("/mvc/clean");

        Assert.Equal("10", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Endpoint_filter_fails_a_minimal_api_handler_with_a_loop(bool useMiddleware)
    {
        await using var app = await TestApp.StartAsync(useMiddleware: useMiddleware);

        var exception = await Assert.ThrowsAsync<NPlusOneViolationException>(() => app.Client.GetAsync("/asserted/loop"));

        Assert.Equal(5, Assert.Single(exception.Report!.Suspects).Count);
    }

    [Fact]
    public async Task Endpoint_filter_passes_a_clean_handler()
    {
        await using var app = await TestApp.StartAsync();

        var response = await app.Client.GetAsync("/asserted/clean");

        Assert.Equal("10", await response.Content.ReadAsStringAsync());
        Assert.Empty(app.Logs.Guard);
    }

    [Fact]
    public async Task Filters_use_the_registered_options()
    {
        await using var app = await TestApp.StartAsync(o => o.RepeatThreshold = 10);

        (await app.Client.GetAsync("/asserted/loop")).EnsureSuccessStatusCode();
        (await app.Client.GetAsync("/mvc/loop")).EnsureSuccessStatusCode();
    }

    // An assert left on an endpoint must not turn data into an outage: in Production it reports instead of failing.
    [Theory]
    [InlineData("/mvc/loop")]
    [InlineData("/asserted/loop")]
    public async Task Filters_log_instead_of_throwing_in_production(string path)
    {
        await using var app = await TestApp.StartAsync(useMiddleware: false, environment: "Production");

        var response = await app.Client.GetAsync(path);

        Assert.Equal("10", await response.Content.ReadAsStringAsync());
        var entry = Assert.Single(app.Logs.Guard);
        Assert.Equal("NPlusOneAssertInProduction", entry.EventName);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, entry.Level);
        Assert.Contains("Query ran 5 times at ShopContext.CountOrdersOneByOneAsync", entry.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/mvc/loop")]
    [InlineData("/asserted/loop")]
    public async Task Filters_throw_in_production_when_asked(string path)
    {
        await using var app = await TestApp.StartAsync(o => o.AssertThrowsInProduction = true, useMiddleware: false, environment: "Production");

        await Assert.ThrowsAsync<NPlusOneViolationException>(() => app.Client.GetAsync(path));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Staging")]
    public async Task Filters_throw_outside_production(string environment)
    {
        await using var app = await TestApp.StartAsync(useMiddleware: false, environment: environment);

        await AssertFailsAsync(app.Client, "/mvc/loop");
        await AssertFailsAsync(app.Client, "/asserted/loop");
    }

    // In Development the host adds the developer exception page, which turns the exception into a 500 that names it.
    private static async Task AssertFailsAsync(HttpClient client, string path)
    {
        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync(path);
        }
        catch (NPlusOneViolationException)
        {
            return;
        }

        Assert.Equal(System.Net.HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains(nameof(NPlusOneViolationException), await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
