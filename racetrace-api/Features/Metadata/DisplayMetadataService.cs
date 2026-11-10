using System.Text.Json;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Features.Metadata;

public sealed class DisplayMetadataService(ISupabaseRestClient client)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        RespectNullableAnnotations = true,
        PropertyNameCaseInsensitive = false,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict,
    };

    public async Task<DisplayMetadataDto> GetAsync(CancellationToken cancellationToken)
    {
        var payload = await client.RpcAsync(
            "get_display_metadata",
            new(),
            SupabaseRole.Anon,
            cancellationToken
        );
        var metadata =
            payload?.Deserialize<DisplayMetadataDto>(JsonOptions)
            ?? throw new JsonException("The display metadata RPC returned no metadata.");

        return metadata with
        {
            Categories = metadata
                .Categories.Select(category =>
                    category with
                    {
                        DatabaseCode = category.Code,
                        Code = PublicApiCodes.ToPublicCategory(category.Code)!,
                        Aliases = Aliases(
                            category,
                            PublicApiCodes.GetCategoryAliases(category.Code)
                        ),
                    }
                )
                .ToArray(),
            SessionTypes = metadata
                .SessionTypes.Select(session =>
                    session with
                    {
                        DatabaseCode = session.Code,
                        Code = PublicApiCodes.ToPublicSessionType(session.Code)!,
                        Aliases = Aliases(
                            session,
                            PublicApiCodes.GetSessionTypeAliases(session.Code)
                        ),
                    }
                )
                .ToArray(),
        };
    }

    private static string[] Aliases(DisplayLabelDto item, IEnumerable<string> publicAliases) =>
        item
            .Aliases.Prepend(item.Code)
            .Concat(publicAliases)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
}
