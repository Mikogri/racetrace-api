namespace RaceTrace.Api.Models;

public sealed record ListResponse<T>(
    IReadOnlyList<T> Data,
    PaginationMetadata Pagination,
    string Sort,
    string Order
);

public sealed record PaginationMetadata(int Page, int Limit, int Total, int TotalPages);
