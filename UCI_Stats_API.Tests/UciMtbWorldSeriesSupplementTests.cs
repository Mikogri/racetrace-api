using System.Text.Json.Nodes;
using UCI_Stats_API.Infrastructure;
using UCI_Stats_API.Services;

namespace UCI_Stats_API.Tests;

public sealed class UciMtbWorldSeriesSupplementTests
{
    [Theory]
    [InlineData("03:24.322", 204_322)]
    [InlineData("01:03:24.322", 3_804_322)]
    public void ParsesUciCumulativeTime(string value, int expected)
    {
        var parsed = UciMtbWorldSeriesSupplementService.TryParseDurationMilliseconds(value, out var milliseconds);

        Assert.True(parsed);
        Assert.Equal(expected, milliseconds);
    }

    [Fact]
    public async Task SameSurnameRidersAreMatchedByExactFullNameAndFinishTime()
    {
        var supabase = new SupplementSupabaseClient(
            Existing("11", "Max", "ALRAN", "1001", 205_000),
            Existing("12", "Till", "ALRAN", "1002", 206_000));
        var pointAwards = new TestPointAwardService();
        var service = new UciMtbWorldSeriesSupplementService(supabase, pointAwards);
        var payload = Payload(
            Source("source-max", "Max ALRAN", "03:25.000", 1,
                ("Split 1", "00:40.000"),
                ("Split 2", "01:30.000"),
                ("Split 3", "02:30.000"),
                ("Split 4", "03:00.000"),
                ("Finish", "03:25.000")),
            Source("source-till", "Till ALRAN", "03:26.000", 2,
                ("Split 1", "00:41.000"),
                ("Finish", "03:26.000")));

        var response = await service.SupplementAsync("25", payload, CancellationToken.None);

        Assert.Equal(2, response.MatchedResults);
        Assert.Equal(["11", "12"], supabase.PatchedResultIds);
        var tillSplits = supabase.PostedSplits
            .Where(row => row["result_id"]!.GetValue<string>() == "12")
            .OrderBy(row => row["split_index"]!.GetValue<int>())
            .ToArray();
        Assert.Equal([1, 5], tillSplits.Select(row => row["split_index"]!.GetValue<int>()));
        Assert.Equal(206_000, tillSplits[1]["race_time"]!.GetValue<int>());
        Assert.Equal(["25"], pointAwards.RecalculatedSessionIds);
        Assert.All(supabase.Roles, role => Assert.Equal(SupabaseRole.Service, role));
    }

    [Fact]
    public async Task MatchingTimeAndSurnameWithoutMatchingFullNameDoesNotUpdateResult()
    {
        var supabase = new SupplementSupabaseClient(
            Existing("11", "Max", "ALRAN", "1001", 205_000));
        var pointAwards = new TestPointAwardService();
        var service = new UciMtbWorldSeriesSupplementService(supabase, pointAwards);
        var payload = Payload(
            Source("source-till", "Till ALRAN", "03:25.000", 1,
                ("Split 1", "00:40.000"),
                ("Finish", "03:25.000")));

        var response = await service.SupplementAsync("25", payload, CancellationToken.None);

        Assert.Equal(0, response.MatchedResults);
        Assert.Equal(1, response.SkippedResults);
        Assert.Empty(supabase.PatchedResultIds);
        Assert.Empty(pointAwards.RecalculatedSessionIds);
    }

    [Fact]
    public async Task UciIdIsPreferredWhenSourceProvidesIt()
    {
        var supabase = new SupplementSupabaseClient(
            Existing("11", "Old", "Name", "1001", 205_000));
        var service = new UciMtbWorldSeriesSupplementService(supabase, new TestPointAwardService());
        var source = Source("source-rider", "Updated Name", "03:26.000", 1,
            ("Split 1", "00:40.000"),
            ("Finish", "03:26.000"));
        source["rider"]!["uciid"] = "1001";

        var response = await service.SupplementAsync("25", Payload(source), CancellationToken.None);

        Assert.Equal(1, response.MatchedResults);
        Assert.Equal(["11"], supabase.PatchedResultIds);
        Assert.Contains(response.Warnings, warning => warning.Contains("matched by UCI ID", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PreferredFirstNameMatchesLongerLegalNameOnlyWithSameUniqueFinishTime()
    {
        var supabase = new SupplementSupabaseClient(
            Existing("11", "Kenneth Ryan", "PINKERTON", "1001", 208_829));
        var service = new UciMtbWorldSeriesSupplementService(supabase, new TestPointAwardService());
        var payload = Payload(
            Source("source-ryan", "Ryan PINKERTON", "03:28.829", 10,
                ("Split 1", "00:40.000"),
                ("Finish", "03:28.829")));

        var response = await service.SupplementAsync("25", payload, CancellationToken.None);

        Assert.Equal(1, response.MatchedResults);
        Assert.Equal(["11"], supabase.PatchedResultIds);
        Assert.Contains(response.Warnings, warning => warning.Contains("compatible preferred rider name", StringComparison.Ordinal));
    }

    private static JsonObject Existing(string id, string firstName, string lastName, string uciId, int raceTimeMs)
    {
        return new JsonObject
        {
            ["id"] = id,
            ["rider_id"] = id,
            ["race_time_ms"] = raceTimeMs,
            ["riders"] = new JsonObject
            {
                ["id"] = id,
                ["uci_id"] = uciId,
                ["first_name"] = firstName,
                ["last_name"] = lastName
            }
        };
    }

    private static JsonObject Source(
        string resultId,
        string riderName,
        string resultTime,
        int position,
        params (string Stage, string Time)[] stages)
    {
        return new JsonObject
        {
            ["resultId"] = resultId,
            ["riderName"] = riderName,
            ["resultPosition"] = position,
            ["resultTime"] = resultTime,
            ["resultInvalidCode"] = null,
            ["rider"] = new JsonObject
            {
                ["fullName"] = riderName,
                ["uciid"] = null
            },
            ["apiRaceStageResults"] = new JsonArray(stages
                .Select(stage => (JsonNode)new JsonObject
                {
                    ["SplitStage"] = stage.Stage,
                    ["Time"] = stage.Time
                })
                .ToArray())
        };
    }

    private static JsonObject Payload(params JsonObject[] results)
    {
        return new JsonObject
        {
            ["error"] = false,
            ["results"] = new JsonArray(results.Select(result => (JsonNode)result).ToArray())
        };
    }

    private sealed class SupplementSupabaseClient(params JsonObject[] existingResults) : ISupabaseRestClient
    {
        public List<string> PatchedResultIds { get; } = [];

        public List<JsonObject> PostedSplits { get; } = [];

        public List<SupabaseRole> Roles { get; } = [];

        public bool PointsRecalculated { get; private set; }

        public Task<SupabaseQueryResult> GetAsync(
            SupabaseQuery query,
            bool count = false,
            SupabaseRole role = SupabaseRole.Anon,
            CancellationToken cancellationToken = default)
        {
            Roles.Add(role);
            Assert.Equal("results", query.Table);
            return Task.FromResult(new SupabaseQueryResult(
                existingResults.Select(row => row.DeepClone().AsObject()).ToArray(),
                existingResults.Length));
        }

        public Task<JsonObject?> GetSingleAsync(
            SupabaseQuery query,
            SupabaseRole role = SupabaseRole.Anon,
            CancellationToken cancellationToken = default)
        {
            Roles.Add(role);
            Assert.Equal("sessions", query.Table);
            return Task.FromResult<JsonObject?>(new JsonObject { ["id"] = 25 });
        }

        public Task<IReadOnlyList<JsonObject>> PostAsync(
            string table,
            JsonNode payload,
            SupabaseRole role,
            string? onConflict = null,
            CancellationToken cancellationToken = default)
        {
            Roles.Add(role);
            Assert.Equal("time_splits", table);
            PostedSplits.AddRange(payload.AsArray().OfType<JsonObject>().Select(row => row.DeepClone().AsObject()));
            return Task.FromResult<IReadOnlyList<JsonObject>>([]);
        }

        public Task<IReadOnlyList<JsonObject>> PatchAsync(
            string table,
            JsonObject payload,
            SupabaseQuery query,
            SupabaseRole role,
            CancellationToken cancellationToken = default)
        {
            Roles.Add(role);
            Assert.Equal("results", table);
            var id = query.Parameters.Single(parameter => parameter.Key == "id").Value[3..];
            PatchedResultIds.Add(id);
            var saved = payload.DeepClone().AsObject();
            saved["id"] = id;
            return Task.FromResult<IReadOnlyList<JsonObject>>([saved]);
        }

        public Task DeleteAsync(
            string table,
            SupabaseQuery query,
            SupabaseRole role,
            CancellationToken cancellationToken = default)
        {
            Roles.Add(role);
            Assert.Equal("time_splits", table);
            return Task.CompletedTask;
        }

        public Task<JsonNode?> RpcAsync(
            string functionName,
            JsonObject payload,
            SupabaseRole role,
            CancellationToken cancellationToken = default)
        {
            Roles.Add(role);
            Assert.Contains(
                functionName,
                new[] { "recalculate_session_points", "refresh_ranking_session_preaggregate" });
            if (functionName == "recalculate_session_points")
            {
                PointsRecalculated = true;
            }
            return Task.FromResult<JsonNode?>(null);
        }
    }
}
