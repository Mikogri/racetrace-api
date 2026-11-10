using System.Text.Json.Serialization;

namespace RaceTrace.Api.Features.Metadata;

public sealed record DisplayMetadataDto
{
    public required IReadOnlyList<CategoryMetadataDto> Categories { get; init; }
    public required IReadOnlyList<SessionTypeMetadataDto> SessionTypes { get; init; }
    public required IReadOnlyList<DisplayLabelDto> RoundStatuses { get; init; }
    public required IReadOnlyList<DisplayLabelDto> SessionStatuses { get; init; }
}

public record DisplayLabelDto
{
    public required string Code { get; init; }
    public required string Name { get; init; }
    public required string ShortName { get; init; }
    public required int DisplayOrder { get; init; }
    public IReadOnlyList<string> Aliases { get; init; } = [];

    [JsonIgnore]
    public string DatabaseCode { get; init; } = "";
}

public sealed record CategoryMetadataDto : DisplayLabelDto
{
    public required string Gender { get; init; }
    public int? MinAge { get; init; }
    public int? MaxAge { get; init; }
}

public sealed record SessionTypeMetadataDto : DisplayLabelDto
{
    public required string Abbreviation { get; init; }
    public required string GroupName { get; init; }
}
