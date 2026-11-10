using System.Diagnostics;
using System.Net;

namespace RaceTrace.Api.Infrastructure.Telemetry;

public sealed class SupabaseTelemetryHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        if (!ApiTelemetry.DetailedEnabled)
            return await base.SendAsync(request, cancellationToken);
        var started = Stopwatch.GetTimestamp();
        var operation =
            request.RequestUri?.AbsolutePath.Contains("/rest/v1/rpc/", StringComparison.Ordinal)
            == true
                ? "rpc"
                : "rest";
        var method = request.Method.Method is "GET" or "POST" or "PATCH" or "DELETE"
            ? request.Method.Method
            : "OTHER";
        var tags = new TagList { { "operation", operation }, { "http.request.method", method } };
        // StartActivity changes AsyncLocal state only inside this method; downstream content carries its own completion.
        using var activity = ApiTelemetry.Activities.StartActivity(
            "supabase.request",
            ActivityKind.Client
        );
        activity?.SetTag("operation", operation);
        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            tags.Add("http.response.status_code", (int)response.StatusCode);
            ApiTelemetry.SupabaseRequests.Add(1, tags);
            ApiTelemetry.SupabaseRetries.Add(0, tags);
            response.Content = new MeasuredContent(response.Content, started, tags);
            activity?.SetTag("http.response.status_code", (int)response.StatusCode);
            if (!response.IsSuccessStatusCode)
                activity?.SetStatus(ActivityStatusCode.Error);
            return response;
        }
        catch (Exception exception)
        {
            tags.Add("http.response.status_code", 0);
            tags.Add(
                "error.type",
                exception is OperationCanceledException ? "cancelled_or_timeout" : "transport"
            );
            ApiTelemetry.SupabaseRequests.Add(1, tags);
            ApiTelemetry.SupabaseDuration.Record(
                Stopwatch.GetElapsedTime(started).TotalSeconds,
                tags
            );
            activity?.SetStatus(ActivityStatusCode.Error);
            throw;
        }
    }

    private sealed class MeasuredContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly long _started;
        private readonly TagList _tags;
        private long _bytes;
        private int _completed;

        public MeasuredContent(HttpContent inner, long started, TagList tags)
        {
            _inner = inner;
            _started = started;
            _tags = tags;
            foreach (var header in inner.Headers)
                Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        private void Count(int bytes)
        {
            Interlocked.Add(ref _bytes, bytes);
            if (bytes == 0)
                Complete();
        }

        private void Complete()
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
                return;
            ApiTelemetry.SupabaseBytes.Add(Interlocked.Read(ref _bytes), _tags);
            ApiTelemetry.SupabaseDuration.Record(
                Stopwatch.GetElapsedTime(_started).TotalSeconds,
                _tags
            );
        }

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context
        )
        {
            using var counted = new CountingStream(stream, Count);
            try
            {
                await _inner.CopyToAsync(counted);
            }
            finally
            {
                Complete();
            }
        }

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken
        )
        {
            using var counted = new CountingStream(stream, Count);
            try
            {
                await _inner.CopyToAsync(counted, cancellationToken);
            }
            finally
            {
                Complete();
            }
        }

        protected override async Task<Stream> CreateContentReadStreamAsync() =>
            new CountingStream(await _inner.ReadAsStreamAsync(), Count);

        protected override async Task<Stream> CreateContentReadStreamAsync(
            CancellationToken cancellationToken
        ) => new CountingStream(await _inner.ReadAsStreamAsync(cancellationToken), Count);

        protected override bool TryComputeLength(out long length)
        {
            length = _inner.Headers.ContentLength ?? 0;
            return _inner.Headers.ContentLength.HasValue;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Complete();
                _inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
