using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

public sealed class RefactorContractTests
{
    private static QueryCollection Query(params (string Key, string Value)[] values) =>
        new(values.ToDictionary(value => value.Key, value => new StringValues(value.Value)));

    [Fact]
    public void MissingAndEmptyFiltersHaveTheSameExistingSemantics()
    {
        var validator = new QueryValidationService();
        var missing = DomainQueryService.MetricsContextFromQuery(Query(), validator);
        var empty = DomainQueryService.MetricsContextFromQuery(
            Query(("season", ""), ("tracks", ""), ("categories", "")), validator);
        Assert.Equal(JsonSerializer.Serialize(missing), JsonSerializer.Serialize(empty));
        Assert.Empty(missing.SeasonYears);
        Assert.Empty(missing.CategoryCodes);
    }

    [Fact]
    public void FilterAliasesPreserveOrderAndForcedStatus()
    {
        var filters = DomainQueryService.MetricsContextFromQuery(
            Query(("seasons", "2026,2023"), ("tracks", "8,2"), ("venue_id", "2,9"),
                ("categories", "elite_men"), ("status", "DNS")),
            new QueryValidationService(), "Finished");
        Assert.Equal([2026, 2023], filters.SeasonYears);
        Assert.Equal(["8", "2", "9"], filters.TrackIds);
        Assert.Equal("Finished", filters.Status);
    }

    [Fact]
    public void PublicResultsKeepSecondsSignedGapsNullsAndCamelCase()
    {
        var row = JsonNode.Parse("""
            {"id":12,"rider_id":7,"status":"Finished","race_time_ms":123456,"gap_ms":-1234,
             "riders":{"first_name":"Loic","last_name":"Bruni"},
             "sectors":[{"split_index":1,"sector_time_ms":42123,"sector_gap_to_best_ms":-12,"sector_position":1}]}
            """)!.AsObject();
        var dto = ResultsService.ToPublicResult(row, "sectors", 1);
        var json = JsonSerializer.SerializeToNode(dto, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        })!;
        Assert.Equal(123.456m, json["finalTime"]!.GetValue<decimal>());
        Assert.Equal(-1.234m, json["finalGapToBest"]!.GetValue<decimal>());
        Assert.Equal(42.123m, json["times"]![0]!["time"]!.GetValue<decimal>());
        Assert.Equal(-0.012m, json["times"]![0]!["gapToBest"]!.GetValue<decimal>());
        Assert.Null(json["teamId"]);
        Assert.Null(json["final_time"]);
        Assert.Equal(123456, row["race_time_ms"]!.GetValue<int>());
    }
}
