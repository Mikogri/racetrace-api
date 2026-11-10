using System.Text;
using System.Text.Json.Nodes;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

public sealed class PointSystemSeederTests
{
    private const string SeedJson = """
        {
          "point_systems": [
            {
              "code": "world_cup_me_2026",
              "name": "World Cup Elite Men 2026",
              "competition_class_code": "world_cup",
              "discipline": "DHI",
              "category_code": "EM",
              "valid_from_season": 2026,
              "valid_to_season": 2026,
              "stages": [
                {
                  "session_type": "FINAL",
                  "awards_points": true,
                  "awards_points_on_season_final_round": true,
                  "rules": [
                    { "position_from": 1, "position_to": 1, "points": 200 },
                    { "position_from": 2, "position_to": 2, "points": 160 }
                  ]
                }
              ]
            }
          ]
        }
        """;

    [Fact]
    public async Task RunningSeederTwiceDoesNotCreateDuplicates()
    {
        var supabase = new StatefulSeedSupabaseClient();
        var seeder = new PointSystemSeeder(supabase);

        var first = await SeedAsync(seeder);
        var second = await SeedAsync(seeder);

        Assert.Equal(new(1, 1, 2), first);
        Assert.Equal(first, second);
        Assert.Single(supabase.PointSystems);
        Assert.Single(supabase.Stages);
        Assert.Equal(2, supabase.Rules.Count);
        Assert.All(supabase.Roles, role => Assert.Equal(SupabaseRole.Service, role));
        Assert.Equal("code", supabase.SystemConflictTarget);
        Assert.Equal("point_system_id,session_type", supabase.StageConflictTarget);
    }

    private static async Task<RaceTrace.Api.Models.PointSystemSeedResult> SeedAsync(
        PointSystemSeeder seeder
    )
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(SeedJson));
        return await seeder.SeedAsync(stream);
    }

    private sealed class StatefulSeedSupabaseClient : ISupabaseRestClient
    {
        public Dictionary<string, JsonObject> PointSystems { get; } = [];
        public Dictionary<string, JsonObject> Stages { get; } = [];
        public List<JsonObject> Rules { get; } = [];
        public List<SupabaseRole> Roles { get; } = [];
        public string? SystemConflictTarget { get; private set; }
        public string? StageConflictTarget { get; private set; }

        public Task<SupabaseQueryResult> GetAsync(
            SupabaseQuery query,
            bool count = false,
            SupabaseRole role = SupabaseRole.Anon,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<JsonObject?> GetSingleAsync(
            SupabaseQuery query,
            SupabaseRole role = SupabaseRole.Anon,
            CancellationToken cancellationToken = default
        )
        {
            Roles.Add(role);
            JsonObject? row = query.Table switch
            {
                "competition_levels" => new() { ["id"] = "1", ["code"] = "world_cup" },
                "categories" => new() { ["id"] = "2", ["code"] = "EM" },
                _ => throw new NotSupportedException(query.Table),
            };
            return Task.FromResult<JsonObject?>(row);
        }

        public Task<IReadOnlyList<JsonObject>> PostAsync(
            string table,
            JsonNode payload,
            SupabaseRole role,
            string? onConflict = null,
            CancellationToken cancellationToken = default
        )
        {
            Roles.Add(role);
            if (table == "points_systems")
            {
                SystemConflictTarget = onConflict;
                var row = payload.AsObject().DeepClone().AsObject();
                row["id"] = "10";
                PointSystems[row.String("code")!] = row;
                return Task.FromResult<IReadOnlyList<JsonObject>>([row]);
            }

            if (table == "point_system_stages")
            {
                StageConflictTarget = onConflict;
                var row = payload.AsObject().DeepClone().AsObject();
                row["id"] = "20";
                Stages[$"{row.String("point_system_id")}:{row.String("session_type")}"] = row;
                return Task.FromResult<IReadOnlyList<JsonObject>>([row]);
            }

            if (table == "point_rules")
            {
                Rules.AddRange(
                    payload
                        .AsArray()
                        .OfType<JsonObject>()
                        .Select(rule => rule.DeepClone().AsObject())
                );
                return Task.FromResult<IReadOnlyList<JsonObject>>([]);
            }

            throw new NotSupportedException(table);
        }

        public Task<IReadOnlyList<JsonObject>> PatchAsync(
            string table,
            JsonObject payload,
            SupabaseQuery query,
            SupabaseRole role,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task DeleteAsync(
            string table,
            SupabaseQuery query,
            SupabaseRole role,
            CancellationToken cancellationToken = default
        )
        {
            Roles.Add(role);
            Assert.Equal("point_rules", table);
            Rules.Clear();
            return Task.CompletedTask;
        }

        public Task<JsonNode?> RpcAsync(
            string functionName,
            JsonObject payload,
            SupabaseRole role,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();
    }
}
