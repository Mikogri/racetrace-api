using System.Text.Json.Nodes;
using UCI_Stats_API.Infrastructure;
using UCI_Stats_API.Models;
using UCI_Stats_API.Services;

namespace UCI_Stats_API.Tests;

public sealed class ChronoRaceBatchImportTests
{
    [Theory]
    [InlineData("elite_men", "male")]
    [InlineData("JM", "male")]
    [InlineData("ME", "male")]
    [InlineData("elite_women", "female")]
    [InlineData("JW", "female")]
    [InlineData("WJ", "female")]
    [InlineData("mixed", null)]
    public void GenderIsDerivedOnlyFromKnownCategory(
        string category,
        string? expected)
    {
        Assert.Equal(expected, ChronoRaceImportService.GenderFromCategory(category));
    }

    [Fact]
    public async Task SessionImportBatchesRidersTeamsResultsAndSplits()
    {
        var supabase = new BatchImportSupabaseClient();
        var service = new ChronoRaceImportService(
            supabase,
            new DomainQueryService(supabase),
            new TestPointAwardService());

        var response = await service.ImportSessionAsync(
            Request(),
            CancellationToken.None);

        Assert.Equal(1, response.RidersCreated);
        Assert.Equal(1, response.RidersUpdated);
        Assert.Equal(2, response.ResultsUpserted);
        Assert.Equal(2, response.SplitsUpserted);
        Assert.True(response.PointsRecalculated);
        Assert.Contains(response.Warnings, warning =>
            warning.Contains("Existing time splits were preserved", StringComparison.Ordinal));

        Assert.Equal(1, supabase.GetCounts["riders"]);
        Assert.Equal(1, supabase.GetCounts["teams"]);
        Assert.Equal(1, supabase.GetCounts["results"]);
        Assert.Equal(1, supabase.PostCounts["riders"]);
        Assert.Equal(1, supabase.PostCounts["teams"]);
        Assert.Equal(1, supabase.PostCounts["results"]);
        Assert.Equal(1, supabase.PostCounts["time_splits"]);
        Assert.Equal(1, supabase.DeleteCounts["time_splits"]);
        Assert.DoesNotContain("riders", supabase.SingleGetCounts.Keys);
        Assert.DoesNotContain("results", supabase.SingleGetCounts.Keys);

        Assert.Equal("uci_id", supabase.OnConflictByTable["riders"]);
        Assert.Equal("code", supabase.OnConflictByTable["teams"]);
        Assert.Equal("session_id,rider_id", supabase.OnConflictByTable["results"]);
        Assert.Equal(2, supabase.PostedRows["riders"].Count);
        Assert.Equal(2, supabase.PostedRows["results"].Count);
        Assert.Equal(2, supabase.PostedRows["time_splits"].Count);
        Assert.Equal(
            ["refresh_rider_team_history"],
            supabase.RpcCalls);
        Assert.All(supabase.PostedRows["time_splits"], row =>
            Assert.Equal("401", row.String("result_id")));
    }

    [Fact]
    public async Task WorldChampionshipJsonUsesTheSameBatchedDatabaseImport()
    {
        var supabase = new BatchImportSupabaseClient();
        var service = new ChronoRaceImportService(
            supabase,
            new DomainQueryService(supabase),
            new TestPointAwardService());

        var response = await service.ImportSessionAsync(
            WorldChampionshipRequest(),
            CancellationToken.None);

        Assert.Equal(2, response.ResultsUpserted);
        Assert.Equal(3, response.SplitsUpserted);
        Assert.Contains(response.Warnings, warning =>
            warning.Contains("World Championship result JSON was normalized", StringComparison.Ordinal));
        Assert.Equal(2, supabase.PostedRows["results"].Count);
        Assert.Equal(240_870, supabase.PostedRows["results"]
            .Single(row => row.String("rider_id") == "102")
            .Int("race_time_ms"));
        Assert.Equal([1, 2], supabase.PostedRows["time_splits"]
            .Where(row => row.String("result_id") == "401")
            .Select(row => row.Int("split_index")));
        Assert.Empty(supabase.RpcCalls);
    }

    [Fact]
    public async Task ImportRejectsConflictingCompetitionClassCodes()
    {
        var supabase = new BatchImportSupabaseClient();
        var service = new ChronoRaceImportService(
            supabase,
            new DomainQueryService(supabase),
            new TestPointAwardService());
        var source = Request();
        var request = source with
        {
            Round = source.Round with
            {
                CompetitionClassCode = "world_championship"
            }
        };

        var exception = await Assert.ThrowsAsync<ApiException>(() =>
            service.ImportSessionAsync(request, CancellationToken.None));

        Assert.Equal("COMPETITION_CLASS_MISMATCH", exception.Code);
    }

    private static ChronoRaceImportRequest Request()
    {
        var riders = new JsonObject
        {
            ["11"] = Rider(11, "1001", "ALRAN", "Max"),
            ["22"] = Rider(22, "1002", "ALRAN", "Till")
        };
        var results = new JsonArray
        {
            Result(11, 1, 240_000, new JsonArray
            {
                new JsonObject { ["RaceTime"] = 45_000, ["Speed"] = 40.5m },
                new JsonObject { ["RaceTime"] = 65_000, ["Speed"] = 42.0m }
            }),
            Result(22, 2, 241_000, new JsonArray())
        };

        return new ChronoRaceImportRequest(
            new ImportRoundPayload("20240701_mtb", 2024, 1, null, null),
            new ImportTrackPayload("Speedster", "Leogang", "AUT", 2400, 18.5m),
            new ImportCompetitionClassPayload("world_cup", "World Cup", 2),
            new ImportConditionPayload(null, "Dry", true),
            "EM",
            new ImportSessionPayload("Final", "FINAL", new DateOnly(2024, 7, 1), "finished"),
            new JsonObject
            {
                ["Riders"] = riders,
                ["Results"] = results
            });
    }

    private static ChronoRaceImportRequest WorldChampionshipRequest()
    {
        return new ChronoRaceImportRequest(
            new ImportRoundPayload(
                "20240901_mtb_world_championship",
                2024,
                1,
                null,
                null,
                CompetitionClassCode: "world_championship"),
            new ImportTrackPayload("Speedster", "Leogang", "AUT", 2400, 18.5m),
            new ImportCompetitionClassPayload("world_championship", "World Championship", 1),
            new ImportConditionPayload(null, "Dry", true),
            "EM",
            new ImportSessionPayload("Final", "FINAL", new DateOnly(2024, 9, 1), "finished"),
            new JsonObject
            {
                ["event"] = new JsonObject
                {
                    ["number"] = 1,
                    ["name"] = "Elite Men Downhill",
                    ["discipline"] = "DOWNHILL"
                },
                ["results"] = new JsonArray
                {
                    WorldResult("1001", 11, "ALRAN Max", 1, "4:00.000", "50.0",
                        new JsonObject { ["resultType"] = "Time", ["value"] = "0:45.000" },
                        new JsonObject { ["resultType"] = "Time", ["value"] = "1:05.000" }),
                    WorldResult("1002", 22, "ALRAN Till", 2, "+0.870", "49.0",
                        new JsonObject { ["resultType"] = "Time", ["value"] = "0:45.500" })
                }
            });
    }

    private static JsonObject WorldResult(
        string uciId,
        int bib,
        string name,
        int rank,
        string time,
        string speed,
        params JsonObject[] splits)
    {
        return new JsonObject
        {
            ["resultType"] = "Time",
            ["key"] = bib.ToString(),
            ["rider"] = new JsonObject
            {
                ["key"] = bib.ToString(),
                ["bib"] = bib,
                ["name"] = name,
                ["nation"] = "FRA",
                ["uciRiderId"] = uciId
            },
            ["splits"] = new JsonArray(splits),
            ["rank"] = rank,
            ["value"] = time,
            ["time"] = time,
            ["gap"] = string.Empty,
            ["speed"] = speed
        };
    }

    private static JsonObject Rider(int raceNr, string uciId, string familyName, string givenName)
    {
        return new JsonObject
        {
            ["RaceNr"] = raceNr,
            ["UciRiderId"] = uciId,
            ["UciCode"] = $"FRA{uciId}",
            ["FamilyName"] = familyName,
            ["GivenName"] = givenName,
            ["Nation"] = "FRA",
            ["UciTeamCode"] = "AON",
            ["UciTeamName"] = "AON Racing"
        };
    }

    private static JsonObject Result(int raceNr, int position, int raceTime, JsonArray times)
    {
        return new JsonObject
        {
            ["RaceNr"] = raceNr,
            ["Position"] = position,
            ["RaceTime"] = raceTime,
            ["CompletedDistance"] = 2400,
            ["Status"] = "Finished",
            ["InResult"] = true,
            ["Times"] = times
        };
    }

    private sealed class BatchImportSupabaseClient : ISupabaseRestClient
    {
        public List<string> RpcCalls { get; } = [];

        public Dictionary<string, int> GetCounts { get; } = [];
        public Dictionary<string, int> SingleGetCounts { get; } = [];
        public Dictionary<string, int> PostCounts { get; } = [];
        public Dictionary<string, int> DeleteCounts { get; } = [];
        public Dictionary<string, string?> OnConflictByTable { get; } = [];
        public Dictionary<string, List<JsonObject>> PostedRows { get; } = [];

        public Task<SupabaseQueryResult> GetAsync(
            SupabaseQuery query,
            bool count = false,
            SupabaseRole role = SupabaseRole.Anon,
            CancellationToken cancellationToken = default)
        {
            Increment(GetCounts, query.Table);
            IReadOnlyList<JsonObject> rows = query.Table switch
            {
                "riders" =>
                [
                    new JsonObject
                    {
                        ["id"] = "101",
                        ["uci_id"] = "1001",
                        ["uci_code"] = "FRA1001",
                        ["first_name"] = "Max",
                        ["last_name"] = "ALRAN"
                    }
                ],
                "teams" => [],
                "results" => [],
                "competition_ranks" =>
                [
                    new JsonObject { ["id"] = "2", ["code"] = "world_cup" },
                    new JsonObject { ["id"] = "9", ["code"] = "world_championship" }
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
            Increment(SingleGetCounts, query.Table);
            JsonObject? row = query.Table switch
            {
                "tracks" => new JsonObject { ["id"] = "1" },
                "competition_ranks" => new JsonObject { ["id"] = "2" },
                "competition_levels" => new JsonObject { ["id"] = "20", ["code"] = "world_cup" },
                "conditions" => new JsonObject { ["id"] = "3" },
                "categories" => new JsonObject { ["id"] = "4", ["code"] = "EM" },
                "rounds" => new JsonObject { ["id"] = "5" },
                "round_categories" => new JsonObject { ["round_id"] = "5", ["category_id"] = "4" },
                "sessions" => new JsonObject { ["id"] = "6" },
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
            Increment(PostCounts, table);
            OnConflictByTable[table] = onConflict;
            var rows = payload.AsArray()
                .OfType<JsonObject>()
                .Select(row => row.DeepClone().AsObject())
                .ToList();
            PostedRows[table] = rows;

            IReadOnlyList<JsonObject> saved = table switch
            {
                "riders" => rows.Select(row => WithId(
                    row,
                    row.String("uci_id") == "1001" ? "101" : "102")).ToArray(),
                "teams" => rows.Select(row => WithId(row, "301")).ToArray(),
                "results" => rows.Select(row => WithId(
                    row,
                    row.String("rider_id") == "101" ? "401" : "402")).ToArray(),
                "time_splits" => [],
                _ => throw new NotSupportedException($"Unexpected POST table '{table}'.")
            };
            return Task.FromResult(saved);
        }

        public Task<IReadOnlyList<JsonObject>> PatchAsync(
            string table,
            JsonObject payload,
            SupabaseQuery query,
            SupabaseRole role,
            CancellationToken cancellationToken = default)
        {
            var ids = new Dictionary<string, string>
            {
                ["tracks"] = "1",
                ["competition_ranks"] = "2",
                ["conditions"] = "3",
                ["rounds"] = "5",
                ["sessions"] = "6"
            };
            return Task.FromResult<IReadOnlyList<JsonObject>>([WithId(payload, ids[table])]);
        }

        public Task DeleteAsync(
            string table,
            SupabaseQuery query,
            SupabaseRole role,
            CancellationToken cancellationToken = default)
        {
            Increment(DeleteCounts, table);
            return Task.CompletedTask;
        }

        public Task<JsonNode?> RpcAsync(
            string functionName,
            JsonObject payload,
            SupabaseRole role,
            CancellationToken cancellationToken = default)
        {
            RpcCalls.Add(functionName);
            Assert.Contains(
                functionName,
                new[] { "refresh_rider_team_history", "recalculate_session_points" });
            Assert.Equal(SupabaseRole.Service, role);
            return Task.FromResult<JsonNode?>(null);
        }

        private static JsonObject WithId(JsonObject payload, string id)
        {
            var saved = payload.DeepClone().AsObject();
            saved["id"] = id;
            return saved;
        }

        private static void Increment(Dictionary<string, int> counts, string table)
        {
            counts[table] = counts.GetValueOrDefault(table) + 1;
        }
    }
}
