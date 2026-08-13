namespace UCI_Stats_API.Models;

public sealed record ApiErrorResponse(ApiError Error);

public sealed record ApiError(string Code, string Message);
