using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RaceTrace.Api.Controllers;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Models;

namespace RaceTrace.Api.Tests;

public sealed class OpenApiContractTests
{
    [Fact]
    public async Task Offline_document_matches_wire_names_nullability_enums_dates_and_queries()
    {
        var builder = WebApplication.CreateBuilder(
            new WebApplicationOptions { EnvironmentName = "ContractTests" }
        );
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddRaceTraceHttp();
        builder.Services.AddControllers().AddApplicationPart(typeof(RankingController).Assembly);
        await using var app = builder.Build();
        app.MapControllers();
        app.MapOpenApi();
        await app.StartAsync();
        try
        {
            using var client = new HttpClient();
            var document = JsonNode.Parse(
                await client.GetStringAsync($"{app.Urls.Single()}/openapi/v1.json")
            )!;
            var schemas = document["components"]!["schemas"]!;
            Assert.NotNull(schemas["ChampionshipContext"]!["properties"]!["sessionTypes"]);
            Assert.Equal("array", schemas["JsonArray"]!["type"]!.GetValue<string>());
            Assert.NotNull(schemas["ListResponseOfJsonObject"]!["properties"]!["data"]!["items"]);
            Assert.Contains(
                "null",
                schemas["ChronoRaceSessionReimportRequest"]!["properties"]![
                    "resultJson"
                ]!.ToJsonString()
            );
            Assert.Equal(
                "integer",
                schemas["RankingRiderDto"]!["properties"]!["riderId"]!["type"]!.GetValue<string>()
            );
            Assert.Contains(
                "null",
                schemas["RankingRiderDto"]!["properties"]!["rating"]!["type"]!.ToJsonString()
            );
            Assert.Contains(
                "male",
                schemas["FantasyRiderDto"]!["properties"]!["gender"]!["enum"]!.ToJsonString()
            );
            Assert.Equal(
                "date",
                schemas["RiderTeamHistoryDto"]!["properties"]!["validFrom"]![
                    "format"
                ]!.GetValue<string>()
            );
            Assert.Contains(
                "previousPosition",
                schemas["FieldSpreadPointDto"]!["required"]!
                    .AsArray()
                    .Select(node => node!.GetValue<string>())
            );
            var ranking = document["paths"]!["/api/v1/ranking"]!["get"]!;
            Assert.Contains(
                ranking["parameters"]!.AsArray(),
                node => node!["name"]!.GetValue<string>() == "season"
            );
            Assert.NotNull(ranking["responses"]!["503"]);
            Assert.NotNull(
                document["paths"]!["/api/admin/import/results/session"]!["post"]!["requestBody"]
            );
            Assert.NotNull(
                document["paths"]!["/api/v1/rounds/{roundId}/image"]!["get"]!["responses"]!["302"]
            );
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public void Every_controller_action_declares_its_success_wire_type()
    {
        var missing = typeof(RankingController)
            .Assembly.GetTypes()
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type))
            .SelectMany(type => type.GetMethods())
            .Where(method => method.GetCustomAttributes<HttpMethodAttribute>().Any())
            .Where(method =>
                !method
                    .GetCustomAttributes<ProducesResponseTypeAttribute>()
                    .Any(response => response.StatusCode == 200 && response.Type != typeof(void))
            )
            .Select(method => $"{method.DeclaringType!.Name}.{method.Name}");
        Assert.Empty(missing);
    }

    [Fact]
    public void Ranking_declares_the_existing_DTO_not_an_anonymous_shape()
    {
        var response = typeof(RankingController)
            .GetMethod(nameof(RankingController.List))!
            .GetCustomAttributes<ProducesResponseTypeAttribute>()
            .SingleOrDefault(type => type.StatusCode == 200);
        Assert.Equal(typeof(RankingResponseDto), response?.Type);
        var recommendation = typeof(FantasyController)
            .GetMethod(nameof(FantasyController.Recommendation))!
            .GetCustomAttributes<ProducesResponseTypeAttribute>()
            .Single(type => type.StatusCode == 200);
        Assert.Equal(typeof(FantasyCurrentDto), recommendation.Type);
    }
}
