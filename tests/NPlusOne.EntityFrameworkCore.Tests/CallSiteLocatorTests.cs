using NPlusOne.EntityFrameworkCore.Recording;

namespace NPlusOne.EntityFrameworkCore.Tests;

public sealed class CallSiteLocatorTests
{
    [Theory]
    [InlineData("<LoadAsync>d__3", "LoadAsync")]
    [InlineData("<Run>b__0_0", "Run")]
    [InlineData("<<Main>$>b__0_0", "Main")]
    [InlineData("<Main>$", "Main")]
    [InlineData("<Outer>g__Local|2_0", "Outer")]
    [InlineData("Plain", "Plain")]
    [InlineData("<>c", "<>c")]
    public void Unwraps_compiler_generated_names(string name, string expected) =>
        Assert.Equal(expected, CallSiteLocator.UnwrapGeneratedName(name));

    [Fact]
    public void Capture_returns_the_first_application_frame()
    {
        var site = CallSiteLocator.Capture(null);

        Assert.StartsWith("CallSiteLocatorTests.Capture_returns_the_first_application_frame (CallSiteLocatorTests.cs:", site, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Capture_names_the_async_method_and_lambda_owner()
    {
        await Task.Yield();
        var fromLambda = new Func<string?>(() => CallSiteLocator.Capture(null))();

        Assert.StartsWith("CallSiteLocatorTests.Capture_names_the_async_method_and_lambda_owner (", fromLambda, StringComparison.Ordinal);
    }
}
