using System.Text.Json.Nodes;

namespace UCI_Stats_API.Models;

public sealed record RankingResponse(
    IReadOnlyList<JsonObject> Data,
    PaginationMetadata Pagination,
    string Sort,
    string Order,
    RankingSummary Summary);

public sealed record RankingSummary(
    int Sessions,
    int Finals,
    int Riders);
