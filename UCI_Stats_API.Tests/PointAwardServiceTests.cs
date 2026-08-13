using System.Text.Json.Nodes;
using UCI_Stats_API.Infrastructure;
using UCI_Stats_API.Services;

namespace UCI_Stats_API.Tests;

public sealed class PointAwardServiceTests
{
    [Fact]
    public async Task SelectsHistoricalSystemAndWritesPointsWithAuditReferences()
    {
        var supabase = new PointAwardSupabaseClient();
        var result = await new PointAwardService(supabase)
            .RecalculateSessionAsync("25", CancellationToken.None);

        Assert.True(result.PointSystemApplied);
        Assert.Equal("world-cup-me-2026", result.PointSystemCode);
        Assert.Equal(2, result.ResultsProcessed);
        Assert.Equal(2, result.PointRowsWritten);
        Assert.Equal(75, result.TotalPoints);
        Assert.True(supabase.DeletedExistingPoints);
        Assert.Equal("result_id,points_system_id", supabase.OnConflict);

        var rows = Assert.IsType<JsonArray>(supabase.PostedPoints);
        Assert.Equal(50, rows[0]!["points_awarded"]!.GetValue<int>());
        Assert.Equal("rule-1", rows[0]!["point_rule_id"]!.GetValue<string>());
        Assert.Equal(25, rows[1]!["points_awarded"]!.GetValue<int>());
        Assert.Equal("rule-2", rows[1]!["point_rule_id"]!.GetValue<string>());
        Assert.All(rows, row =>
        {
            Assert.Equal("system-1", row!["points_system_id"]!.GetValue<string>());
            Assert.Equal("stage-final", row["point_system_stage_id"]!.GetValue<string>());
        });

        Assert.Contains(supabase.SystemQuery!.Parameters, parameter =>
            parameter.Key == "valid_from_season" && parameter.Value == "lte.2026");
        Assert.Contains(supabase.SystemQuery.Parameters, parameter =>
            parameter.Key == "valid_to_season" && parameter.Value == "gte.2026");
        Assert.Contains(supabase.SystemQuery.Parameters, parameter =>
            parameter.Key == "competition_level_id" && parameter.Value == "eq.2");
        Assert.Contains(supabase.SystemQuery.Parameters, parameter =>
            parameter.Key == "category_id" && parameter.Value == "eq.4");
    }

    [Fact]
    public async Task SeasonFinalRoundWritesZeroWhenStageDoesNotAwardFinalRoundPoints()
    {
        var supabase = new PointAwardSupabaseClient(
            isSeasonFinalRound: true,
            awardsPointsOnSeasonFinalRound: false);

        var result = await new PointAwardService(supabase)
            .RecalculateSessionAsync("25", CancellationToken.None);

        Assert.True(result.PointSystemApplied);
        Assert.Equal(0, result.TotalPoints);
        var rows = Assert.IsType<JsonArray>(supabase.PostedPoints);
        Assert.All(rows, row =>
        {
            Assert.Equal(0, row!["points_awarded"]!.GetValue<int>());
            Assert.Null(row["point_rule_id"]);
        });
    }

    [Fact]
    public async Task MoreThanOneMatchingSystemIsRejected()
    {
        var supabase = new PointAwardSupabaseClient(ambiguousSystems: true);

        var exception = await Assert.ThrowsAsync<ApiException>(() =>
            new PointAwardService(supabase).RecalculateSessionAsync("25", CancellationToken.None));

        Assert.Equal("AMBIGUOUS_POINT_SYSTEM", exception.Code);
        Assert.Null(supabase.PostedPoints);
        Assert.False(supabase.DeletedExistingPoints);
    }

    private sealed class PointAwardSupabaseClient(
        bool isSeasonFinalRound = false,
        bool awardsPointsOnSeasonFinalRound = true,
        bool ambiguousSystems = false) : ISupabaseRestClient
    {
        public SupabaseQuery? SystemQuery { get; private set; }
        public JsonNode? PostedPoints { get; private set; }
        public string? OnConflict { get; private set; }
        public bool DeletedExistingPoints { get; private set; }

        public Task<SupabaseQueryResult> GetAsync(
            SupabaseQuery query,
            bool count = false,
            SupabaseRole role = SupabaseRole.Anon,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(SupabaseRole.Service, role);
            IReadOnlyList<JsonObject> rows = query.Table switch
            {
                "results" =>
                [
                    Result("result-1", 1),
                    Result("result-2", 2)
                ],
                "points_systems" => Systems(query),
                "point_rules" =>
                [
                    Rule("rule-1", 1, 1, 50),
                    Rule("rule-2", 2, 2, 25)
                ],
                _ => throw new NotSupportedException($"Unexpected GET table '{query.Table}'.")
            };

            return Task.FromResult(new SupabaseQueryResult(rows, rows.Count));
        }

        public Task<JsonObject?> GetSingleAsync(
            SupabaseQuery query,
            SupabaseRole role = SupabaseRole.Anon,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(SupabaseRole.Service, role);
            JsonObject row = query.Table switch
            {
                "sessions" => Session(),
                "point_system_stages" => new JsonObject
                {
                    ["id"] = "stage-final",
                    ["point_system_id"] = "system-1",
                    ["session_type"] = "FINAL",
                    ["awards_points"] = true,
                    ["awards_points_on_season_final_round"] = awardsPointsOnSeasonFinalRound
                },
                _ => throw new NotSupportedException($"Unexpected single GET table '{query.Table}'.")
            };

            return Task.FromResult<JsonObject?>(row);
        }

        public Task<IReadOnlyList<JsonObject>> PostAsync(
            string table,
            JsonNode payload,
            SupabaseRole role,
            string? onConflict = null,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(SupabaseRole.Service, role);
            Assert.Equal("result_points", table);
            PostedPoints = payload.DeepClone();
            OnConflict = onConflict;
            return Task.FromResult<IReadOnlyList<JsonObject>>([]);
        }

        public Task DeleteAsync(
            string table,
            SupabaseQuery query,
            SupabaseRole role,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(SupabaseRole.Service, role);
            Assert.Equal("result_points", table);
            DeletedExistingPoints = true;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<JsonObject>> PatchAsync(
            string table,
            JsonObject payload,
            SupabaseQuery query,
            SupabaseRole role,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<JsonNode?> RpcAsync(
            string functionName,
            JsonObject payload,
            SupabaseRole role,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        private JsonObject Session()
        {
            return new JsonObject
            {
                ["id"] = "25",
                ["category_id"] = "4",
                ["session_type"] = "FINAL",
                ["round_categories"] = new JsonObject
                {
                    ["rounds"] = new JsonObject
                    {
                        ["id"] = "10",
                        ["season_year"] = 2026,
                        ["competition_level_id"] = "2",
                        ["is_season_final_round"] = isSeasonFinalRound,
                        ["competition_levels"] = new JsonObject
                        {
                            ["id"] = "2",
                            ["code"] = "world_cup",
                            ["priority"] = 2
                        }
                    },
                    ["categories"] = new JsonObject
                    {
                        ["id"] = "4",
                        ["code"] = "EM"
                    }
                }
            };
        }

        private IReadOnlyList<JsonObject> Systems(SupabaseQuery query)
        {
            SystemQuery = query;
            var first = new JsonObject
            {
                ["id"] = "system-1",
                ["code"] = "world-cup-me-2026",
                ["name"] = "World Cup Elite Men 2026"
            };
            return ambiguousSystems
                ? [first, new JsonObject { ["id"] = "system-2", ["code"] = "duplicate" }]
                : [first];
        }

        private static JsonObject Result(string id, int position) => new()
        {
            ["id"] = id,
            ["position"] = position,
            ["is_classified"] = true,
            ["status"] = "Finished"
        };

        private static JsonObject Rule(string id, int from, int? to, int points) => new()
        {
            ["id"] = id,
            ["position_from"] = from,
            ["position_to"] = to,
            ["points"] = points
        };
    }
}
