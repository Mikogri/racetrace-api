using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using UCI_Stats_API.Controllers;
using UCI_Stats_API.Infrastructure;
using UCI_Stats_API.Services;

namespace UCI_Stats_API.Tests;

public sealed class AdminDataServiceTests
{
    [Fact]
    public async Task TrackPatchNormalizesValuesAndUsesServiceRole()
    {
        var supabase = new AdminSupabaseClient();
        supabase.Rows["tracks"] = new JsonObject
        {
            ["id"] = 1,
            ["name"] = "Old Track",
            ["venue"] = "Old Venue",
            ["country_code"] = "FRA",
            ["length_m"] = 2200,
            ["avg_slope"] = 15m
        };
        var service = new AdminDataService(supabase);

        var updated = await service.UpdateAsync(
            "tracks",
            "1",
            new JsonObject
            {
                ["name"] = "  New Track  ",
                ["country_code"] = " fra "
            },
            CancellationToken.None);

        Assert.Equal("New Track", updated["name"]!.GetValue<string>());
        Assert.Equal("FRA", updated["country_code"]!.GetValue<string>());
        Assert.Equal(1, supabase.PatchCount);
        Assert.All(supabase.Roles, role => Assert.Equal(SupabaseRole.Service, role));
    }

    [Fact]
    public async Task TrackPatchRejectsDuplicateNameIgnoringCase()
    {
        var supabase = new AdminSupabaseClient
        {
            ConflictingTrack = new JsonObject
            {
                ["id"] = 2,
                ["name"] = "Black Snake"
            }
        };
        supabase.Rows["tracks"] = new JsonObject
        {
            ["id"] = 1,
            ["name"] = "Old Track",
            ["venue"] = "Val di Sole",
            ["country_code"] = "ITA",
            ["length_m"] = 2200,
            ["avg_slope"] = 18m
        };
        var service = new AdminDataService(supabase);

        var exception = await Assert.ThrowsAsync<ApiException>(() => service.UpdateAsync(
            "tracks",
            "1",
            new JsonObject { ["name"] = " black snake " },
            CancellationToken.None));

        Assert.Equal(StatusCodes.Status409Conflict, exception.StatusCode);
        Assert.Equal("TRACK_NAME_ALREADY_EXISTS", exception.Code);
        Assert.Equal(0, supabase.PatchCount);
    }

    [Fact]
    public async Task UnknownOrGeneratedFieldIsRejectedBeforeDatabaseCall()
    {
        var supabase = new AdminSupabaseClient();
        var service = new AdminDataService(supabase);

        var exception = await Assert.ThrowsAsync<ApiException>(() => service.UpdateAsync(
            "tracks",
            "1",
            new JsonObject { ["created_at"] = "2026-01-01" },
            CancellationToken.None));

        Assert.Equal("ADMIN_FIELDS_NOT_EDITABLE", exception.Code);
        Assert.Empty(supabase.Roles);
    }

    [Fact]
    public async Task SessionCategoryPatchCreatesMissingRoundCategoryRelationship()
    {
        var supabase = new AdminSupabaseClient();
        supabase.Rows["sessions"] = new JsonObject
        {
            ["id"] = 5,
            ["name"] = "Q1",
            ["status"] = "finished",
            ["round_id"] = 2,
            ["category_id"] = 3,
            ["session_type"] = "Q1",
            ["session_date"] = "2026-07-10"
        };
        supabase.Rows["rounds"] = new JsonObject { ["id"] = 2 };
        supabase.Rows["categories"] = new JsonObject { ["id"] = 9 };
        var service = new AdminDataService(supabase);

        var updated = await service.UpdateAsync(
            "sessions",
            "5",
            new JsonObject { ["category_id"] = 9 },
            CancellationToken.None);

        Assert.Equal(9, updated["category_id"]!.GetValue<int>());
        Assert.Equal(1, supabase.RoundCategoryPostCount);
        Assert.Equal("2", supabase.PostedRoundCategory!["round_id"]!.GetValue<string>());
        Assert.Equal("9", supabase.PostedRoundCategory["category_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task RoundTrackPatchDoesNotRequireValidatingLegacyRoundStatus()
    {
        var supabase = new AdminSupabaseClient();
        supabase.Rows["rounds"] = new JsonObject
        {
            ["id"] = 12,
            ["status"] = "",
            ["track_id"] = 3
        };
        supabase.Rows["tracks"] = new JsonObject { ["id"] = 7 };
        var service = new AdminDataService(supabase);

        var updated = await service.UpdateRoundTrackAsync(
            "12",
            new JsonObject { ["track_id"] = 7 },
            CancellationToken.None);

        Assert.Equal(12, updated["roundId"]!.GetValue<long>());
        Assert.Equal(7, updated["trackId"]!.GetValue<long>());
        Assert.Equal(7, supabase.Rows["rounds"]["track_id"]!.GetValue<long>());
        Assert.Equal(1, supabase.PatchCount);
        Assert.All(supabase.Roles, role => Assert.Equal(SupabaseRole.Service, role));
    }

    [Fact]
    public async Task RiderPatchCanUpdateOnlyProfileImageUrl()
    {
        var supabase = new AdminSupabaseClient();
        supabase.Rows["riders"] = Rider();
        var service = new AdminDataService(supabase);

        var updated = await service.UpdateRiderAsync(
            "7",
            new JsonObject
            {
                ["profileImageUrl"] = "  https://images.example.com/rider.png  "
            },
            CancellationToken.None);

        Assert.Equal(
            "https://images.example.com/rider.png",
            updated.String("profile_image_url"));
        Assert.False(updated.Bool("has_injury"));
        Assert.Equal(1, supabase.PatchCount);
    }

    [Fact]
    public async Task RiderPatchCanUpdateOnlyInjuryFlag()
    {
        var supabase = new AdminSupabaseClient();
        supabase.Rows["riders"] = Rider();
        var service = new AdminDataService(supabase);

        var updated = await service.UpdateRiderAsync(
            "7",
            new JsonObject { ["hasInjury"] = true },
            CancellationToken.None);

        Assert.True(updated.Bool("has_injury"));
        Assert.Null(updated["profile_image_url"]);
        Assert.Equal(1, supabase.PatchCount);
    }

    [Fact]
    public async Task RiderPatchRejectsDuplicateCamelAndSnakeCaseField()
    {
        var service = new AdminDataService(new AdminSupabaseClient());

        var exception = await Assert.ThrowsAsync<ApiException>(() =>
            service.UpdateRiderAsync(
                "7",
                new JsonObject
                {
                    ["hasInjury"] = true,
                    ["has_injury"] = false
                },
                CancellationToken.None));

        Assert.Equal("DUPLICATE_ADMIN_FIELD", exception.Code);
    }

    [Fact]
    public async Task RiderPatchRejectsFieldsOutsideProfileImageAndInjury()
    {
        var service = new AdminDataService(new AdminSupabaseClient());

        var exception = await Assert.ThrowsAsync<ApiException>(() =>
            service.UpdateRiderAsync(
                "7",
                new JsonObject { ["first_name"] = "Changed" },
                CancellationToken.None));

        Assert.Equal("ADMIN_FIELDS_NOT_EDITABLE", exception.Code);
    }

    [Fact]
    public void AdminControllerExposesDedicatedRiderPatchRoute()
    {
        var action = typeof(AdminDataController).GetMethod(nameof(AdminDataController.UpdateRider));
        var route = Assert.Single(action!.GetCustomAttributes<HttpPatchAttribute>());

        Assert.Equal("riders/{riderId:long}", route.Template);
    }

    [Fact]
    public void AdminControllerExposesDedicatedRoundDeleteRoute()
    {
        var action = typeof(AdminDataController).GetMethod(nameof(AdminDataController.DeleteRound));
        var route = Assert.Single(action!.GetCustomAttributes<HttpDeleteAttribute>());

        Assert.Equal("rounds/{roundId:long}", route.Template);
    }

    [Fact]
    public void AdminControllerExposesDedicatedRoundTrackPatchRoute()
    {
        var action = typeof(AdminDataController).GetMethod(nameof(AdminDataController.UpdateRoundTrack));
        var route = Assert.Single(action!.GetCustomAttributes<HttpPatchAttribute>());

        Assert.Equal("rounds/{roundId:long}/track", route.Template);
    }

    [Fact]
    public async Task RoundDeleteUsesAtomicDatabaseFunctionWithServiceRole()
    {
        var supabase = new AdminSupabaseClient
        {
            RpcResponse = new JsonObject
            {
                ["roundId"] = 12,
                ["sessionsDeleted"] = 4,
                ["resultsDeleted"] = 97,
                ["splitsDeleted"] = 485
            }
        };
        supabase.Rows["rounds"] = new JsonObject { ["id"] = 12 };
        var service = new AdminDataService(supabase);

        var summary = await service.DeleteRoundAsync("12", CancellationToken.None);

        Assert.Equal(12, summary["roundId"]!.GetValue<int>());
        Assert.Equal("delete_round_cascade", supabase.RpcFunctionName);
        Assert.Equal(12, supabase.RpcPayload!["target_round_id"]!.GetValue<long>());
        Assert.Equal(SupabaseRole.Service, supabase.RpcRole);
    }

    [Fact]
    public async Task RoundDeleteExplainsWhenDatabaseMigrationIsMissing()
    {
        var supabase = new AdminSupabaseClient
        {
            RpcException = new SupabaseApiException(
                StatusCodes.Status404NotFound,
                """{"code":"PGRST202","message":"Could not find the function public.delete_round_cascade(target_round_id) in the schema cache"}""")
        };
        supabase.Rows["rounds"] = new JsonObject { ["id"] = 12 };
        var service = new AdminDataService(supabase);

        var exception = await Assert.ThrowsAsync<ApiException>(() =>
            service.DeleteRoundAsync("12", CancellationToken.None));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, exception.StatusCode);
        Assert.Equal("ROUND_DELETE_MIGRATION_REQUIRED", exception.Code);
    }

    [Fact]
    public void AdminControllerProtectsEveryActionWithAdminAuthorizationFilter()
    {
        var filter = typeof(AdminDataController)
            .GetCustomAttributes<ServiceFilterAttribute>()
            .Single();

        Assert.Equal(typeof(AdminAuthorizationFilter), filter.ServiceType);
    }

    [Fact]
    public void EditableResourcesDoNotExposeDatabaseManagedFields()
    {
        var service = new AdminDataService(new AdminSupabaseClient());

        var resources = service.EditableResources();
        var tracks = Assert.IsType<JsonArray>(resources["tracks"]);

        Assert.Contains(tracks, field => field!.GetValue<string>() == "name");
        Assert.DoesNotContain(tracks, field => field!.GetValue<string>() is "id" or "created_at" or "updated_at");
    }

    [Fact]
    public void EditableResourcesExposeCompetitionClassesInsteadOfLegacyNames()
    {
        var resources = new AdminDataService(new AdminSupabaseClient()).EditableResources();
        var rounds = Assert.IsType<JsonArray>(resources["rounds"]);

        Assert.NotNull(resources["competition-classes"]);
        Assert.Null(resources["competition-levels"]);
        Assert.Null(resources["competition-ranks"]);
        Assert.Contains(rounds, field => field!.GetValue<string>() == "competition_class_id");
        Assert.DoesNotContain(rounds, field => field!.GetValue<string>() is "competition_level_id" or "competition_rank_id");
    }

    private static JsonObject Rider() => new()
    {
        ["id"] = 7,
        ["uci_id"] = 10000000007,
        ["uci_code"] = "FRA20000101",
        ["last_name"] = "Rider",
        ["first_name"] = "Test",
        ["nation"] = "FRA",
        ["birth_date"] = "2000-01-01",
        ["is_protected"] = false,
        ["has_injury"] = false,
        ["profile_image_url"] = null,
        ["profile_image_updated_at"] = null
    };

    private sealed class AdminSupabaseClient : ISupabaseRestClient
    {
        public Dictionary<string, JsonObject> Rows { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<SupabaseRole> Roles { get; } = [];

        public int PatchCount { get; private set; }

        public int RoundCategoryPostCount { get; private set; }

        public JsonObject? PostedRoundCategory { get; private set; }

        public JsonObject? ConflictingTrack { get; init; }

        public JsonNode? RpcResponse { get; init; }

        public SupabaseApiException? RpcException { get; init; }

        public string? RpcFunctionName { get; private set; }

        public JsonObject? RpcPayload { get; private set; }

        public SupabaseRole? RpcRole { get; private set; }

        public Task<SupabaseQueryResult> GetAsync(
            SupabaseQuery query,
            bool count = false,
            SupabaseRole role = SupabaseRole.Anon,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<JsonObject?> GetSingleAsync(
            SupabaseQuery query,
            SupabaseRole role = SupabaseRole.Anon,
            CancellationToken cancellationToken = default)
        {
            Roles.Add(role);
            if (query.Table == "round_categories")
            {
                return Task.FromResult<JsonObject?>(null);
            }

            if (query.Table == "tracks" &&
                query.Parameters.Any(parameter =>
                    parameter.Key == "name" &&
                    parameter.Value.StartsWith("ilike.", StringComparison.OrdinalIgnoreCase)))
            {
                return Task.FromResult(ConflictingTrack?.DeepClone().AsObject());
            }

            return Task.FromResult(
                Rows.TryGetValue(query.Table, out var row)
                    ? row.DeepClone().AsObject()
                    : null);
        }

        public Task<IReadOnlyList<JsonObject>> PostAsync(
            string table,
            JsonNode payload,
            SupabaseRole role,
            string? onConflict = null,
            CancellationToken cancellationToken = default)
        {
            Roles.Add(role);
            if (table == "round_categories")
            {
                RoundCategoryPostCount++;
                PostedRoundCategory = payload.DeepClone().AsObject();
                return Task.FromResult<IReadOnlyList<JsonObject>>([PostedRoundCategory]);
            }

            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<JsonObject>> PatchAsync(
            string table,
            JsonObject payload,
            SupabaseQuery query,
            SupabaseRole role,
            CancellationToken cancellationToken = default)
        {
            Roles.Add(role);
            PatchCount++;
            var updated = Rows[table].DeepClone().AsObject();
            foreach (var property in payload)
            {
                updated[property.Key] = property.Value?.DeepClone();
            }

            Rows[table] = updated.DeepClone().AsObject();
            return Task.FromResult<IReadOnlyList<JsonObject>>([updated]);
        }

        public Task DeleteAsync(
            string table,
            SupabaseQuery query,
            SupabaseRole role,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<JsonNode?> RpcAsync(
            string functionName,
            JsonObject payload,
            SupabaseRole role,
            CancellationToken cancellationToken = default)
        {
            RpcFunctionName = functionName;
            RpcPayload = payload.DeepClone().AsObject();
            RpcRole = role;
            Roles.Add(role);
            if (RpcException is not null)
            {
                throw RpcException;
            }

            return Task.FromResult(RpcResponse?.DeepClone());
        }
    }
}
