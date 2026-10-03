# Read-path parity probe

Developer-only console probe. Normal reads are guarded to GET and `get_*` RPCs. `--prepare` is an explicit service-role operation that prepares existing cached sessions; do not use it for read-only measurements. Credentials come from the API config/user secrets, never from fixtures.

Run from repository root (PowerShell):

```powershell
dotnet run --project tools/ReadPathParity -c Release -- --parity --capture --api-path <original-api-directory> --output <baseline-directory>
dotnet run --project tools/ReadPathParity -c Release -- --parity --prepared --typed --api-path <api-directory> --output <baseline-directory>
```

For an independent old implementation build, use `-p:Baseline=true -p:ApiProject=<original-csproj> --artifacts-path <separate-directory>` and run its DLL. Do not build old/new implementations into the same artifacts directory.

Useful options for `--parity`:

- `--cases rank-all,rank-2026,bruni-all,asa`: subset.
- `--repeat 3 --preagg-only`: repeated fresh application-cache requests against prepared database data.
- `--fixtures <ignored-directory>`: capture read responses, rejecting changes to an already captured response.
- Add `--replay` to read those fixtures without networking. Missing fixtures fail the run; no network fallback.

Keep snapshots, recordings and benchmark output in ignored scratch directories. Fixtures contain rider data, not request authorization headers. Live timing includes network and fixture recording overhead; offline replay includes file reads. Neither is an isolated SQL benchmark or frontend end-to-end measurement. The older `--stages`/service-only probe modes are retained for diagnostics; use `--parity` for the new flags and correctness comparisons.
