using System.Text.Json;

namespace Ancilla.Core.Tests;

public class LocalizedStringConverterTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Reads_localised_objects_plain_strings_and_nulls()
    {
        const string json = """
            {
              "TrackId": "careerRank1",
              "Name": { "value": "Career Rank", "translations": { "fr-FR": "Rang de carrière" } },
              "XpPerRank": 1000,
              "Ranks": [
                { "Rank": 1, "XpRequiredForRank": 500, "RankTitle": { "Value": "Recruit" }, "RankSubTitle": "Bronze", "RankTier": null },
                { "Rank": 2, "XpRequiredForRank": 750, "RankTitle": { "translations": {} }, "RankSubTitle": 7, "RankTier": "1" }
              ]
            }
            """;
        var def = JsonSerializer.Deserialize<RewardTrackDefinition>(json, Web)!;
        Assert.Equal("Career Rank", def.Name);
        Assert.Equal("Recruit", def.Ranks[0].RankTitle);
        Assert.Equal("Bronze", def.Ranks[0].RankSubTitle);
        Assert.Null(def.Ranks[0].RankTier);
        Assert.Null(def.Ranks[1].RankTitle);      // object without a value
        Assert.Null(def.Ranks[1].RankSubTitle);   // a number isn't text
        Assert.Equal("1", def.Ranks[1].RankTier);
    }

    [Fact]
    public void Writes_a_plain_string()
    {
        var medal = new MedalDefinition(1, "Killjoy", "End an enemy's killing spree.", 0, 0, 0, 0, 0);
        var json = JsonSerializer.Serialize(medal);
        Assert.Contains("\"Name\":\"Killjoy\"", json);
        Assert.Equal(medal, JsonSerializer.Deserialize<MedalDefinition>(json));
    }
}
