namespace UCI_Stats_API.Infrastructure;

public sealed class SupabaseApiException : Exception
{
    public SupabaseApiException(int statusCode, string responseBody)
        : base($"Supabase request failed with status {statusCode}.")
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    public int StatusCode { get; }

    public string ResponseBody { get; }
}
