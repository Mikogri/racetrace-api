# Performance monitoring

RaceTrace uses `System.Diagnostics.Metrics`, `ActivitySource`, structured `ILogger`
events and OpenTelemetry. No collector, account or paid service is required to run
the API. Metrics never use rider IDs, query strings, cache keys, full URLs,
credentials or payloads as dimensions.

## Configuration

Configuration keys (environment variables use `__` instead of `:`):

| Key | Default | Meaning |
| --- | --- | --- |
| `Telemetry:Enabled` | `true` | Enable the telemetry pipeline and endpoint measurements |
| `Telemetry:Detailed` | `true` | Enable calculator, cache and Supabase detail |
| `Telemetry:LogRequests` | `false` | Emit one structured completion event per request |
| `Telemetry:TraceSampleRatio` | `0.1` | Sample 10% of root traces; honor parent sampling |
| `Telemetry:OtlpEndpoint` | unset | Optional OTLP/gRPC collector endpoint, e.g. `http://localhost:4317` |

Metrics are not sampled. Request logging is independent of trace sampling and off
by default. `Detailed=false` retains endpoint counts/durations and job health but
skips detailed stages. Setting no exporter creates no outgoing telemetry traffic.
An unavailable optional collector does not block startup or request completion;
delivery can be lost while it is unavailable. Never put credentials in the endpoint URL.

## Local inspection

Install the official .NET diagnostic tool if needed, then find the API process:

```powershell
dotnet tool install --global dotnet-counters
dotnet-counters ps
dotnet-counters monitor --process-id <pid> --counters RaceTrace.Api
```

For structured per-request logging, set `Telemetry__LogRequests=true` before
starting the API. Each event contains the HTTP method, route **template**, status,
elapsed seconds and uncompressed body bytes. For distributed traces or persistent
histograms, point `Telemetry__OtlpEndpoint` at a local OpenTelemetry collector or
Aspire dashboard's OTLP/gRPC endpoint; collectors are optional and not bundled.

## Instruments and interpretation

| Instrument | Interpretation |
| --- | --- |
| `racetrace.http.requests` | Completed requests, including Output Cache responses; route/method/status |
| `racetrace.http.duration` | Whole application request pipeline duration, seconds |
| `racetrace.http.errors` | HTTP 5xx responses; error rate = errors / requests |
| `racetrace.http.timeouts` | Upstream timeouts translated by exception middleware, plus HTTP 504 |
| `racetrace.http.response.body.size` | Bytes written before compression, not HTTP headers or compressed wire bytes |
| `racetrace.supabase.requests` | Physical application HTTP attempts; REST/RPC, method and status (0 for transport failure) |
| `racetrace.supabase.duration` | Network + body consumption, seconds; **not SQL execution time** |
| `racetrace.supabase.downloaded.bytes` | Body bytes consumed after HTTP automatic decompression |
| `racetrace.supabase.retries` | Transport retries; currently zero: no automatic transport retry is configured |
| `racetrace.operation.duration` | Coarse calculator/preparation stage duration, seconds |
| `racetrace.cache.requests` | memory/output/preaggregation, outcome hit/miss/bypass/unavailable/stale/fallback |
| `racetrace.singleflight.calls` | `new` versus `shared`; these are work sharing, not cache hits |
| `racetrace.singleflight.waiters` | Active shared waiters (up/down counter) |
| `racetrace.singleflight.wait.duration` | Shared waiter duration, including cancellation/failure, seconds |
| `racetrace.job.runs` | success/failure/lease_conflict/lease_loss/skipped/cancelled |
| `racetrace.job.duration` | Completed job duration, seconds |
| `racetrace.job.retries` | Explicit job retries only; next scheduled runs are not retries |
| `racetrace.job.last_success` | Process-observed latest success as Unix seconds; absent until first success |

Operation `rating` is the whole metrics batch in `PaceService` (after input fetching
on the raw path), including its Pace/Consistency dependencies; it is not a timer
around each tiny rating formula. `pace` measures a field/session or a rider's complete
Pace aggregation, `consistency` a rider's complete history, `podium` the complete Monte
Carlo simulation, and `round_analytics` the complete round calculation. Nested stages
overlap: do not sum them to derive total endpoint latency. `preaggregation` includes
the prepared-field load/calculation/publish workflow, so it can include HTTP time.
`preaggregation_rebuild` wraps both raw refresh and preparation and is the total rebuild timer.

Supabase pagination creates multiple physical requests. Repeated logical reads,
fallback RPCs and pages are not mislabeled as retries. If a retry policy is added,
place the handler **inside** that policy and increment retries at the actual retry
decision. Runtime-internal TCP connection recovery is below this measurement boundary.
Supabase spans cover the send/response-header phase; the duration histogram covers
body consumption through EOF/copy completion or response disposal. Partially consumed
streams report only bytes actually consumed. No body is buffered solely for telemetry.

Endpoint middleware must stay **after** response compression and **before** exception
handling and Output Cache. This records decompressed response bodies and mapped error
statuses, including hits. Endpoints that never consult Output Cache are `bypass`, not
`miss`. These byte counts are not compressed sizes and not Supabase's billed egress.

Histogram bucket boundaries in seconds are 0.001, 0.005, 0.01, 0.025, 0.05, 0.1,
0.25, 0.5, 1, 2.5, 5, 10, 30, 60, 120. Average = exported sum/count. P50/P95/P99
are approximate quantiles from exported buckets (for Prometheus-compatible storage,
use `histogram_quantile` over a rate of buckets, grouped by route or operation).
Quantiles are **not** averages of per-instance percentiles. `dotnet-counters` is for
live diagnostics; persistent cross-instance percentiles need a collector/backend.

The last-success gauge resets on restart; durable job history is the database lease
record. Alert on the age of last success relative to the configured job interval,
failure rate, lease losses and persistent absence of success after deployment.
SingleFlight is process-local: 20 identical requests on one instance produce one
`new` and 19 `shared` only while their execution overlaps.

## Verification and overhead

```powershell
dotnet test racetrace-api.Tests/racetrace-api.Tests.csproj --filter FullyQualifiedName~PerformanceTelemetryTests
```

The tests use real Meter listeners and local/fake transports, not production secrets
or Supabase. They check byte counts with non-ASCII payloads, deferred reads, bounded
dimensions, mapped timeouts, cached responses and 20-request SingleFlight fan-in.
An isolated stopwatch probe prints the incremental cost of the operation scope with
and without an active listener. It is a local microbenchmark, not proof of production
P95/P99 or a full endpoint baseline. Capture comparable traffic and collector settings
before claiming a production latency improvement. No production benchmark is performed
by these tests.

### Numerical parity against a preserved baseline

The independent `tools/CalculatorParity` console harness loads each API assembly in
its own assembly context. Pass an already-preserved, pre-change build and a current
build; it does not rebuild or modify either input:

```powershell
dotnet run --project tools/CalculatorParity -- <baseline-api.dll> <current-api.dll>
```

It checks full serialized outputs with exact equality (no floating-point tolerance):
four eight-rider/five-sector Pace fields (clean, incident, DNF, qualification), eight
Pace aggregates, three Consistency histories, 30 Rating boundary combinations, and
Podium calibration/simulation for ordinary and world championship conditions. Monte
Carlo uses seed 17432 and 5000 iterations. The command exits nonzero on any mismatch
and prints input assembly hashes and individual result hashes. These fixed fixtures
provide reproducible parity evidence, not a claim to cover every possible race.
