using RaceTrace.Api.Models;

namespace RaceTrace.Api.Services;

public static class ResponseFactory
{
    public static ListResponse<T> Page<T>(
        IReadOnlyList<T> data,
        int page,
        int limit,
        int? total,
        string sort,
        string order
    )
    {
        var safeTotal = total ?? data.Count;
        var totalPages = safeTotal == 0 ? 0 : (int)Math.Ceiling(safeTotal / (double)limit);
        return new ListResponse<T>(
            data,
            new PaginationMetadata(page, limit, safeTotal, totalPages),
            sort,
            order
        );
    }
}
