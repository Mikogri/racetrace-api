using System.Text.Json.Serialization;

namespace RaceTrace.Api.Features.Metadata;

public sealed record DisplayMetadataDto
{
    [JsonPropertyName("categories")] public required IReadOnlyList<CategoryMetadataDto> Categories { get; init; }
    [JsonPropertyName("sessionTypes")] public required IReadOnlyList<SessionTypeMetadataDto> SessionTypes { get; init; }
    [JsonPropertyName("roundStatuses")] public required IReadOnlyList<DisplayLabelDto> RoundStatuses { get; init; }
    [JsonPropertyName("sessionStatuses")] public required IReadOnlyList<DisplayLabelDto> SessionStatuses { get; init; }
}

public record DisplayLabelDto
{
    [JsonPropertyName("code")] public required string Code { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("shortName")] public required string ShortName { get; init; }
    [JsonPropertyName("displayOrder")] public required int DisplayOrder { get; init; }
    [JsonPropertyName("aliases")] public IReadOnlyList<string> Aliases { get; init; } = [];
    [JsonIgnore] public string DatabaseCode { get; init; } = "";
}

public sealed record CategoryMetadataDto : DisplayLabelDto
{
    [JsonPropertyName("gender")] public required string Gender { get; init; }
    [JsonPropertyName("minAge")] public int? MinAge { get; init; }
    [JsonPropertyName("maxAge")] public int? MaxAge { get; init; }
}

public sealed record SessionTypeMetadataDto : DisplayLabelDto
{
    [JsonPropertyName("abbreviation")] public required string Abbreviation { get; init; }
    [JsonPropertyName("groupName")] public required string GroupName { get; init; }
}
