using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RaceTrace.Api.Controllers;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Models;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

public sealed class ApiCamelCaseIntegrationTests
{
    [Fact]
    public async Task CamelCaseAdminPatchTranslatesOnlyAtControllerBoundary()
    {
        using var provider = Services();
        var options = provider
            .GetRequiredService<IOptions<JsonOptions>>()
            .Value.JsonSerializerOptions;
        var patch = JsonSerializer.Deserialize<JsonObject>(
            """{"countryCode":" fra ","lengthM":2300}""",
            options
        )!;
        var database = new DatabaseClient();
        database.Row.Clear();
        database.Row["id"] = 1;
        database.Row["name"] = "Track";
        database.Row["venue"] = "Val di Sole";
        database.Row["country_code"] = "ITA";
        database.Row["length_m"] = 2200;
        database.Row["avg_slope"] = 15m;
        var controller = new AdminDataController(new AdminDataService(database), null!, null!);

        var response = Assert.IsType<OkObjectResult>(
            await controller.Update("tracks", "1", patch, default)
        );
        var json = JsonSerializer.SerializeToElement(response.Value, options);

        Assert.Equal("FRA", json.GetProperty("countryCode").GetString());
        Assert.Equal(2300, json.GetProperty("lengthM").GetInt32());
        Assert.Equal("FRA", database.PatchPayload!["country_code"]!.GetValue<string>());
        Assert.NotNull(database.PatchPayload["length_m"]);
        Assert.False(database.PatchPayload.ContainsKey("countryCode"));
        Assert.True(patch.ContainsKey("countryCode"));
        Assert.False(patch.ContainsKey("country_code"));
    }

    [Fact]
    public async Task CamelCaseAdminRequestBindsAndKeepsSupabaseRpcArgumentsUnchanged()
    {
        using var provider = Services();
        var options = provider
            .GetRequiredService<IOptions<JsonOptions>>()
            .Value.JsonSerializerOptions;
        var request = JsonSerializer.Deserialize<PointsRecalculateRequest>(
            """{"sessionId":"123","seasonYear":2026,"categoryCode":"EM","scope":"world_cup_overall"}""",
            options
        )!;
        var database = new DatabaseClient();
        var awards = new PointAwards();
        var controller = Controller(database, awards);

        var response = Assert.IsType<OkObjectResult>(
            await controller.Recalculate(request, default)
        );
        var json = JsonSerializer.SerializeToElement(response.Value, options);

        Assert.Equal("123", awards.SessionId);
        Assert.Equal(2026, request.SeasonYear);
        Assert.Equal("EM", request.CategoryCode);
        Assert.Equal("refresh_ranking_session_preaggregate", database.RpcName);
        Assert.Equal(123L, database.RpcPayload!["p_session_id"]!.GetValue<long>());
        Assert.False(database.RpcPayload.ContainsKey("pSessionId"));
        Assert.Equal(1, json.GetProperty("sessionsRecalculated").GetInt32());
        Assert.Equal("123", json.GetProperty("sessionIds")[0].GetProperty("sessionId").GetString());
        Assert.False(json.TryGetProperty("sessions_recalculated", out _));
    }

    [Fact]
    public async Task PaginatedControllerResponseConvertsNestedRowsWithoutMutatingDatabaseNamesOrValues()
    {
        using var provider = Services();
        var options = provider
            .GetRequiredService<IOptions<JsonOptions>>()
            .Value.JsonSerializerOptions;
        var database = new DatabaseClient();
        var controller = Controller(database, new PointAwards());

        var response = Assert.IsType<OkObjectResult>(await controller.CompetitionClasses(default));
        var json = JsonSerializer.SerializeToElement(response.Value, options);

        Assert.Equal(1, json.GetProperty("pagination").GetProperty("totalPages").GetInt32());
        var item = json.GetProperty("data")[0];
        Assert.True(item.GetProperty("isActive").GetBoolean());
        Assert.Equal("ELITE_MEN", item.GetProperty("categoryCode").GetString());
        Assert.Equal(
            "2026-01-01",
            item.GetProperty("metadata").GetProperty("validFrom").GetString()
        );
        Assert.False(item.TryGetProperty("is_active", out _));
        Assert.Equal("competition_levels", database.Query!.Table);
        Assert.Contains(
            database.Query.Parameters,
            pair => pair.Key == "order" && pair.Value == "priority.asc"
        );
        Assert.True(database.Row.ContainsKey("is_active"));
        Assert.False(database.Row.ContainsKey("isActive"));
        Assert.NotNull(database.Row["metadata"]!["valid_from"]);
    }

    private static ServiceProvider Services() =>
        new ServiceCollection().AddLogging().AddRaceTraceHttp().BuildServiceProvider();

    private static PointsController Controller(DatabaseClient database, PointAwards awards) =>
        new(
            new PointsService(
                database,
                new QueryValidationService(),
                new DomainQueryService(database),
                awards
            )
        )
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

    private sealed class PointAwards : IPointAwardService
    {
        public string? SessionId { get; private set; }

        public Task<PointAwardCalculationResult> RecalculateSessionAsync(
            string sessionId,
            CancellationToken cancellationToken = default
        )
        {
            SessionId = sessionId;
            return Task.FromResult(
                new PointAwardCalculationResult(sessionId, false, 0, 0, 0, null, null)
            );
        }
    }

    private sealed class DatabaseClient : ISupabaseRestClient
    {
        public JsonObject Row { get; } =
            new()
            {
                ["id"] = 1,
                ["priority"] = 1,
                ["is_active"] = true,
                ["category_code"] = "ELITE_MEN",
                ["metadata"] = new JsonObject { ["valid_from"] = "2026-01-01" },
            };
        public SupabaseQuery? Query { get; private set; }
        public string? RpcName { get; private set; }
        public JsonObject? RpcPayload { get; private set; }
        public JsonObject? PatchPayload { get; private set; }

        public Task<SupabaseQueryResult> GetAsync(
            SupabaseQuery query,
            bool count = false,
            SupabaseRole role = SupabaseRole.Anon,
            CancellationToken cancellationToken = default
        )
        {
            Query = query;
            return Task.FromResult(new SupabaseQueryResult([Row], 1));
        }

        public Task<JsonObject?> GetSingleAsync(
            SupabaseQuery query,
            SupabaseRole role = SupabaseRole.Anon,
            CancellationToken cancellationToken = default
        ) =>
            Task.FromResult<JsonObject?>(
                query.Table == "tracks" ? Row : new JsonObject { ["session_id"] = 123 }
            );

        public Task<JsonNode?> RpcAsync(
            string functionName,
            JsonObject payload,
            SupabaseRole role,
            CancellationToken cancellationToken = default
        )
        {
            RpcName = functionName;
            RpcPayload = payload;
            return Task.FromResult<JsonNode?>(null);
        }

        public Task<IReadOnlyList<JsonObject>> PostAsync(
            string table,
            JsonNode payload,
            SupabaseRole role,
            string? onConflict = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<JsonObject>> PatchAsync(
            string table,
            JsonObject payload,
            SupabaseQuery query,
            SupabaseRole role,
            CancellationToken cancellationToken = default
        )
        {
            PatchPayload = payload;
            var updated = Row.DeepClone().AsObject();
            foreach (var item in payload)
                updated[item.Key] = item.Value?.DeepClone();
            return Task.FromResult<IReadOnlyList<JsonObject>>([updated]);
        }

        public Task DeleteAsync(
            string table,
            SupabaseQuery query,
            SupabaseRole role,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();
    }
}
