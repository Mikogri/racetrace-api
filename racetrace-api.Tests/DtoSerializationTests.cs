using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Models;

namespace RaceTrace.Api.Tests;

public sealed class DtoSerializationTests
{
    [Fact]
    public void FantasyRulesUseCanonicalNamesWithWebOptions()
    {
        var rules = new FantasyRulesDto(2026, "test", 100m, 2, 1, 1,
            [new FantasyPlacementDto(1, 10, 10)], [new FantasyBonusDto(1, 5)], false, "test");
        var expected = JsonNode.Parse("""{"season":2026,"version":"test","budget":100,"teamSize":2,"men":1,"women":1,"finalPlacementPoints":[{"position":1,"menPoints":10,"womenPoints":10}],"q1Bonus":[{"position":1,"points":5}],"dynamicPricing":false,"sourceUrl":"test"}""");

        Assert.True(JsonNode.DeepEquals(expected, JsonSerializer.SerializeToNode(rules, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
    }

    [Fact]
    public void PublicResultsAndPaginationUseCamelCase()
    {
        using var services = new ServiceCollection().AddLogging().AddRaceTraceHttp().BuildServiceProvider();
        var options = services.GetRequiredService<IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>().Value.JsonSerializerOptions;
        var result = new ResultTimeDto(1, "S1", null, -1.25m, 2);
        var expected = JsonNode.Parse("""{"index":1,"name":"S1","time":null,"gapToLeader":-1.25,"position":2,"gapToBest":-1.25}""");

        Assert.True(JsonNode.DeepEquals(expected, JsonSerializer.SerializeToNode(result, options)));
        Assert.True(JsonNode.DeepEquals(expected, JsonSerializer.SerializeToNode(result, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
        var pagination = JsonSerializer.SerializeToNode(new PaginationMetadata(1, 30, 90, 3), options)!;
        Assert.Equal(3, pagination["totalPages"]!.GetValue<int>());
        Assert.Null(pagination["total_pages"]);
    }

    [Fact]
    public void RequestRecordsUseCamelCaseAndKeepOptionalValues()
    {
        using var services = new ServiceCollection().AddLogging().AddRaceTraceHttp().BuildServiceProvider();
        var options = services.GetRequiredService<IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>().Value.JsonSerializerOptions;
        var request = JsonSerializer.Deserialize<PointsRecalculateRequest>("""{"seasonYear":2026,"categoryCode":"EM"}""", options)!;
        Assert.Equal(2026, request.SeasonYear);
        Assert.Equal("EM", request.CategoryCode);
        Assert.Null(request.SessionId);
        Assert.Null(request.Scope);
    }
}
