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
}
