using RaceTrace.Api.Models;

namespace RaceTrace.Api.Infrastructure;

public sealed class ApiExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ApiExceptionMiddleware> _logger;

    public ApiExceptionMiddleware(RequestDelegate next, ILogger<ApiExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (ApiException exception)
        {
            context.Response.StatusCode = exception.StatusCode;
            if (exception.StatusCode == StatusCodes.Status401Unauthorized)
            {
                context.Response.Headers.WWWAuthenticate = "Bearer";
            }

            await context.Response.WriteAsJsonAsync(new ApiErrorResponse(new ApiError(exception.Code, exception.Message)));
        }
        catch (SupabaseApiException exception)
        {
            _logger.LogWarning(exception, "Supabase request failed with status {StatusCode}. Body: {ResponseBody}", exception.StatusCode, exception.ResponseBody);
            var statusCode = exception.StatusCode == StatusCodes.Status503ServiceUnavailable
                ? StatusCodes.Status503ServiceUnavailable
                : StatusCodes.Status500InternalServerError;
            var code = statusCode == StatusCodes.Status503ServiceUnavailable ? "DATABASE_UNAVAILABLE" : "DATABASE_ERROR";
            var message = statusCode == StatusCodes.Status503ServiceUnavailable ? "Database is temporarily unavailable." : "Database request failed.";

            context.Response.StatusCode = statusCode;
            await context.Response.WriteAsJsonAsync(new ApiErrorResponse(new ApiError(code, message)));
        }
        catch (OperationCanceledException exception) when (context.RequestAborted.IsCancellationRequested)
        {
            _logger.LogInformation(exception, "API request was cancelled by the client.");
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = 499;
            }
        }
        catch (TaskCanceledException exception)
        {
            _logger.LogWarning(exception, "External service request timed out or was cancelled.");
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new ApiErrorResponse(new ApiError("UPSTREAM_TIMEOUT", "External service request timed out or was cancelled.")));
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Unhandled API exception");
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(new ApiErrorResponse(new ApiError("INTERNAL_ERROR", "Unexpected server error.")));
        }
    }
}
