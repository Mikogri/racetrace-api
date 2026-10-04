using System.Text.Json.Nodes;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

public sealed class TeamImportTests
{
    [Fact]
    public void ChronoRaceTeamIsTrimmedAndNormalized()
    {
        var rider = new JsonObject
        {
            ["UciTeamCode"] = " aon ",
            ["UciTeamName"] = "  AON RACING  "
        };

        var team = ChronoRaceImportService.ParseChronoRaceTeam(rider);

        Assert.NotNull(team);
        Assert.Equal("AON", team.Value.Code);
        Assert.Equal("AON RACING", team.Value.Name);
    }

    [Fact]
    public void TeamIdIsUsedAsFallbackAndCodeAsFallbackName()
    {
        var rider = new JsonObject
        {
            ["UciTeamCode"] = "",
            ["UciTeamId"] = "ytr",
            ["UciTeamName"] = ""
        };

        var team = ChronoRaceImportService.ParseChronoRaceTeam(rider);

        Assert.NotNull(team);
        Assert.Equal("YTR", team.Value.Code);
        Assert.Equal("YTR", team.Value.Name);
    }

    [Fact]
    public void TeamCodeMayContainDigits()
    {
        var rider = new JsonObject
        {
            ["UciTeamCode"] = " fw5 ",
            ["UciTeamName"] = "Frameworks Racing"
        };

        var team = ChronoRaceImportService.ParseChronoRaceTeam(rider);

        Assert.NotNull(team);
        Assert.Equal("FW5", team.Value.Code);
        Assert.Equal("Frameworks Racing", team.Value.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("AB")]
    [InlineData("TEAM")]
    [InlineData("A-1")]
    public void MissingOrInvalidTeamCodeIsIgnored(string code)
    {
        var rider = new JsonObject { ["UciTeamCode"] = code };

        Assert.Null(ChronoRaceImportService.ParseChronoRaceTeam(rider));
    }

    [Fact]
    public void ResultPayloadContainsHistoricalTeamId()
    {
        var result = new JsonObject
        {
            ["RaceTime"] = 185123,
            ["CompletedDistance"] = 2100,
            ["Status"] = "OK",
            ["Speed"] = 62.4m
        };
        var rider = new JsonObject { ["RaceNr"] = 17 };

        var payload = ChronoRaceImportService.BuildResultPayload(
            "session-1",
            "rider-1",
            "team-7",
            result,
            rider,
            position: 2,
            isClassified: true);

        Assert.Equal("team-7", payload["team_id"]!.GetValue<string>());
        Assert.Equal("rider-1", payload["rider_id"]!.GetValue<string>());
        Assert.Equal(17, payload["race_nr"]!.GetValue<int>());
    }

    [Fact]
    public void ResultPayloadExplicitlyClearsTeamWhenChronoRaceHasNone()
    {
        var payload = ChronoRaceImportService.BuildResultPayload(
            "session-1",
            "rider-1",
            null,
            new JsonObject(),
            new JsonObject { ["RaceNr"] = 17 },
            position: null,
            isClassified: false);

        Assert.True(payload.ContainsKey("team_id"));
        Assert.Null(payload["team_id"]);
    }
}
