using System.Text.Json.Nodes;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

public sealed class UciWorldChampionshipJsonAdapterTests
{
    [Fact]
    public void NormalizesRidersAbsoluteTimesGapsSplitsAndStatuses()
    {
        var normalized = ImportResultJsonNormalizer.Normalize(
            WorldChampionshipJson(),
            "world_championship",
            "EM");

        Assert.Equal(ImportResultJsonNormalizer.UciWorldChampionshipFormat, normalized.Format);
        var riders = normalized.Json.Object("Riders")!;
        var jackson = riders.Object("10053812449")!;
        Assert.Equal(3, jackson.Int("RaceNr"));
        Assert.Equal("GOLDSTONE", jackson.String("FamilyName"));
        Assert.Equal("Jackson", jackson.String("GivenName"));
        Assert.Equal("CAN", jackson.String("Nation"));

        var results = normalized.Json.Array("Results")!.OfType<JsonObject>().ToArray();
        Assert.Equal(5, results.Length);
        Assert.Equal(182_101, results[0].Int("RaceTime"));
        Assert.Equal(182_971, results[1].Int("RaceTime"));
        Assert.Equal(249_542, results[2].Int("RaceTime"));
        Assert.Equal(51.256m, results[0].Decimal("Speed"));
        Assert.True(results[0].Bool("InResult"));

        var winnerSplits = results[0].Array("Times")!.OfType<JsonObject>().ToArray();
        Assert.Equal(26_410, winnerSplits[0].Int("RaceTime"));
        Assert.Equal(77_121, winnerSplits[1].Int("RaceTime"));
        Assert.Equal([1, 2], winnerSplits.Select(split => split.Int("SplitIndex")));

        Assert.Equal("DNF", results[3].String("Status"));
        Assert.Null(results[3].Int("RaceTime"));
        Assert.Single(results[3].Array("Times")!);
        Assert.Equal("DNS", results[4].String("Status"));
        Assert.Empty(results[4].Array("Times")!);
    }

    [Theory]
    [InlineData("26.410", 26_410)]
    [InlineData("1:17.121", 77_121)]
    [InlineData("+0.870", 870)]
    [InlineData("+1:07.441", 67_441)]
    [InlineData("1:02:03.004", 3_723_004)]
    public void ParsesUciTimeFormats(string value, int expectedMilliseconds)
    {
        Assert.Equal(expectedMilliseconds, UciWorldChampionshipJsonAdapter.ParseTimeMilliseconds(value));
    }

    [Fact]
    public void RejectsWorldChampionshipJsonForWorldCupRound()
    {
        var exception = Assert.Throws<ApiException>(() => ImportResultJsonNormalizer.Normalize(
            WorldChampionshipJson(),
            "world_cup",
            "EM"));

        Assert.Equal("RESULT_FORMAT_COMPETITION_CLASS_MISMATCH", exception.Code);
    }

    [Fact]
    public void RejectsCategoryThatDoesNotMatchRecognizedEventName()
    {
        var exception = Assert.Throws<ApiException>(() => ImportResultJsonNormalizer.Normalize(
            WorldChampionshipJson(),
            "world_championship",
            "EW"));

        Assert.Equal("RESULT_FORMAT_CATEGORY_MISMATCH", exception.Code);
    }

    [Fact]
    public void LeavesChronoRaceJsonUnchanged()
    {
        var source = new JsonObject
        {
            ["Riders"] = new JsonObject(),
            ["Results"] = new JsonArray()
        };

        var normalized = ImportResultJsonNormalizer.Normalize(source, "world_cup", "EM");

        Assert.Equal(ImportResultJsonNormalizer.ChronoRaceFormat, normalized.Format);
        Assert.Same(source, normalized.Json);
    }

    private static JsonObject WorldChampionshipJson()
    {
        return new JsonObject
        {
            ["heats"] = new JsonArray(),
            ["event"] = new JsonObject
            {
                ["number"] = 1,
                ["name"] = "Men Elite Downhill",
                ["discipline"] = "DOWNHILL"
            },
            ["results"] = new JsonArray
            {
                Result("3", 3, "GOLDSTONE Jackson", "CAN", "10053812449", "Time", 1, "3:02.101", "51.256",
                    Split("UCI 1", "Time", "26.410", 4),
                    Split("UCI 2", "Time", "1:17.121", 2)),
                Result("7", 7, "PIERRON Amaury", "FRA", "10008827283", "Time", 2, "+0.870", "49.836",
                    Split("UCI 1", "Time", "26.574", 6)),
                Result("2", 2, "BRUNI Loic", "FRA", "10007544358", "Time", 3, "+1:07.441", "50.114",
                    Split("UCI 1", "Time", "26.123", 1)),
                Result("99", 99, "CALONACI Tommaso", "ITA", "10083242047", "IRM", 0, "DNF", null,
                    Split("UCI 1", "Time", "28.653", 63)),
                Result("100", 100, "ORELLANA Gonzalo", "CHI", "10127414029", "IRM", 0, "DNS", null,
                    Split("UCI 1", "Points", "", 0))
            },
            ["resultType"] = "Time"
        };
    }

    private static JsonObject Result(
        string key,
        int bib,
        string name,
        string nation,
        string uciId,
        string resultType,
        int rank,
        string value,
        string? speed,
        params JsonObject[] splits)
    {
        return new JsonObject
        {
            ["resultType"] = resultType,
            ["key"] = key,
            ["rider"] = new JsonObject
            {
                ["key"] = key,
                ["bib"] = bib,
                ["name"] = name,
                ["nation"] = nation,
                ["uciRiderId"] = uciId
            },
            ["splits"] = new JsonArray(splits),
            ["rank"] = rank,
            ["value"] = value,
            ["time"] = value,
            ["gap"] = string.Empty,
            ["speed"] = speed
        };
    }

    private static JsonObject Split(string key, string resultType, string value, int rank)
    {
        return new JsonObject
        {
            ["key"] = key,
            ["name"] = key,
            ["resultType"] = resultType,
            ["value"] = value,
            ["rank"] = rank
        };
    }
}
