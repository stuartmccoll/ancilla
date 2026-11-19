using System.Text.Json;
using Ancilla.Core;
using Ancilla.Web.Services;

namespace Ancilla.Web.Tests;

public class FmtTests
{
    [Fact]
    public void Numbers_and_percentages()
    {
        Assert.Equal("1,117", Fmt.N(1117L));
        Assert.Equal("3.14", Fmt.N(3.14159, 2));
        Assert.Equal("45.7%", Fmt.Pct(45.678));
        Assert.Equal("46%", Fmt.Pct(45.678, 0));
    }

    [Fact]
    public void Css_numbers_always_use_a_full_stop()
    {
        var saved = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");
            Assert.Equal("12.50", Fmt.Css(12.5));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = saved; }
    }

    [Theory]
    [InlineData(247_000L, "4:07")]
    [InlineData(5_000L, "0:05")]
    [InlineData(3_725_000L, "1:02:05")]
    public void Match_clock(long ms, string shown) => Assert.Equal(shown, Fmt.Clock(ms));

    [Theory]
    [InlineData("P9DT18H33M4.5S", "9d 18h 33m")]
    [InlineData("PT2H5M", "2h 5m")]
    [InlineData("PT11M12.5S", "11m 12s")]
    [InlineData("PT12.34S", "12.3s")]
    [InlineData(null, "0.0s")]
    public void Durations(string? iso, string shown) => Assert.Equal(shown, Fmt.Duration(iso));

    [Fact]
    public void Ago_counts_up_then_shows_a_date()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal("just now", Fmt.Ago(now.AddSeconds(-10)));
        Assert.Equal("5m ago", Fmt.Ago(now.AddMinutes(-5.5)));
        Assert.Equal("3h ago", Fmt.Ago(now.AddHours(-3.2)));
        Assert.Equal("2d ago", Fmt.Ago(now.AddDays(-2.5)));
        var old = now.AddDays(-30);
        Assert.Equal(old.ToLocalTime().ToString("d MMM yyyy"), Fmt.Ago(old));
    }

    [Theory]
    [InlineData(1, "1st")] [InlineData(2, "2nd")] [InlineData(3, "3rd")] [InlineData(4, "4th")]
    [InlineData(11, "11th")] [InlineData(12, "12th")] [InlineData(13, "13th")]
    [InlineData(21, "21st")] [InlineData(22, "22nd")] [InlineData(111, "111th")] [InlineData(117, "117th")]
    public void Ordinals(int n, string shown) => Assert.Equal(shown, Fmt.Ordinal(n));

    [Fact]
    public void Outcomes_and_team_names()
    {
        Assert.Equal("Win", Fmt.Outcome(MatchOutcome.Win));
        Assert.Equal("Left", Fmt.Outcome(MatchOutcome.Left));
        Assert.Equal("—", Fmt.Outcome(MatchOutcome.Unknown));
        Assert.Equal("loss", Fmt.OutcomeClass(MatchOutcome.Loss));
        Assert.Equal("Eagle", Fmt.TeamName(0));
        Assert.Equal("Cobra", Fmt.TeamName(1));
        Assert.Equal("Team 3", Fmt.TeamName(2));
    }

    [Theory]
    [InlineData("FlagCaptureAssists", "Flag capture assists")]
    [InlineData("KDA", "KDA")]
    [InlineData("MaxKillingSpree", "Max killing spree")]
    [InlineData("EMPAssists", "EMP assists")]
    public void Humanize_splits_words_and_keeps_acronyms(string name, string shown) => Assert.Equal(shown, Fmt.Humanize(name));

    [Fact]
    public void Group_titles_and_stat_labels()
    {
        Assert.Equal("Capture the Flag", Fmt.GroupTitle("CaptureTheFlagStats"));
        Assert.Equal("PvE (Firefight)", Fmt.GroupTitle("PveStats"));
        Assert.Equal("Grunt birthday party", Fmt.GroupTitle("GruntBirthdayPartyStats"));
        Assert.Equal("Points scored with the skull", Fmt.StatLabel("SkullScoringTicks"));
        Assert.Equal("Skull grabs", Fmt.StatLabel("SkullGrabs"));
    }

    [Fact]
    public void Json_values_and_zeroes()
    {
        JsonElement J(string json) => JsonDocument.Parse(json).RootElement;
        Assert.Equal("1,234", Fmt.Json(J("1234")));
        Assert.Equal("2.50", Fmt.Json(J("2.5")));
        Assert.Equal("1m 30s", Fmt.Json(J("\"PT1M30S\"")));
        Assert.Equal("Sierra 117", Fmt.Json(J("\"Sierra 117\"")));
        Assert.Equal("Yes", Fmt.Json(J("true")));
        Assert.Equal("—", Fmt.Json(J("null")));

        Assert.True(Fmt.IsZero(J("0")));
        Assert.True(Fmt.IsZero(J("\"PT0S\"")));
        Assert.True(Fmt.IsZero(J("null")));
        Assert.False(Fmt.IsZero(J("0.1")));
        Assert.False(Fmt.IsZero(J("\"Blood Gulch\"")));
    }
}
