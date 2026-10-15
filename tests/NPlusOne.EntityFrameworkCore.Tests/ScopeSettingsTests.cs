using System.Text.RegularExpressions;
using NPlusOne.EntityFrameworkCore.Recording;

namespace NPlusOne.EntityFrameworkCore.Tests;

public sealed class ScopeSettingsTests
{
    // Ignored patterns run on the SQL text, which can carry inlined values (EF.Constant, raw SQL): a pattern that
    // backtracks must not hold the request's thread.
    [Fact]
    public void Ignored_patterns_without_a_timeout_get_one()
    {
        var options = new NPlusOneOptions();
        options.IgnoredSqlPatterns.Add(new Regex("FROM \"Orders\"", RegexOptions.IgnoreCase));

        var pattern = Assert.Single(ScopeSettings.From(options).IgnoredSqlPatterns);

        Assert.Equal(ScopeSettings.PatternTimeout, pattern.MatchTimeout);
        Assert.Equal(RegexOptions.IgnoreCase, pattern.Options);
        Assert.Matches(pattern, "select * from \"orders\"");
        Assert.Same(pattern, Assert.Single(ScopeSettings.From(options).IgnoredSqlPatterns));
    }

    [Fact]
    public void Ignored_patterns_keep_their_own_timeout()
    {
        var options = new NPlusOneOptions();
        var own = new Regex("FROM \"Orders\"", RegexOptions.None, TimeSpan.FromSeconds(2));
        options.IgnoredSqlPatterns.Add(own);

        Assert.Same(own, Assert.Single(ScopeSettings.From(options).IgnoredSqlPatterns));
    }

    // 30 characters: seconds of backtracking without a timeout, so undoing the fix fails this test instead of hanging it.
    [Fact]
    public void A_backtracking_pattern_stops_at_the_timeout()
    {
        var options = new NPlusOneOptions();
        options.IgnoredSqlPatterns.Add(new Regex("^(a+)+$"));
        var settings = ScopeSettings.From(options);

        Assert.Throws<RegexMatchTimeoutException>(() => settings.Accepts(NPlusOneSuspectKind.Query, new string('a', 30) + "!"));
    }
}
