param(
    [string]$Baseline = 'http://127.0.0.1:5017',
    [string]$Candidate = 'http://127.0.0.1:5018'
)

$ErrorActionPreference = 'Stop'
$secretFile = Join-Path $env:APPDATA 'Microsoft/UserSecrets/racetrace-api-2026/secrets.json'
$settings = Get-Content -LiteralPath $secretFile -Raw | ConvertFrom-Json
$headers = @{ Authorization = "Bearer $($settings.'ApiAccess:ReadSecret')" }
$paths = @(
    '/api/v1/riders/18/overview?category=elite_men',
    '/api/v1/riders/183/overview?season=2026&category=elite_men',
    '/api/v1/riders/60/overview?season=2025,2026&category=elite_men',
    '/api/v1/riders/183/overview',
    '/api/v1/riders/18/overview?season=2026&category=elite_men&sessionType=FINAL',
    '/api/v1/ranking?category=elite_men&sort=rating&order=desc&pageSize=100&comparison=previousRound',
    '/api/v1/ranking?season=2026&category=elite_men&sort=totalPoints&order=desc&pageSize=100&comparison=defaultFilters',
    '/api/v1/ranking?season=2025&category=elite_women&sort=pace&order=desc&pageSize=100&comparison=previousFilters&comparisonSeason=2026&comparisonCategory=elite_women',
    '/api/v1/ranking?season=2022,2023,2024&category=elite_men&sort=rating&order=desc&pageSize=100&comparison=previousRound'
)

# Both servers must use the same database/configuration. No imports during this read-only check.
# First-in-series is not necessarily cold: prior requests may have warmed overlapping caches.
$failures = 0
foreach ($path in $paths) {
    foreach ($run in 1..2) {
        $measurements = foreach ($server in @($Baseline, $Candidate)) {
            $watch = [Diagnostics.Stopwatch]::StartNew()
            $response = Invoke-WebRequest ($server + $path) -Headers $headers -TimeoutSec 120
            $watch.Stop()
            [pscustomobject]@{
                Seconds = [math]::Round($watch.Elapsed.TotalSeconds, 3)
                Hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
                    [Text.Encoding]::UTF8.GetBytes($response.Content)))
            }
        }
        $equal = $measurements[0].Hash -eq $measurements[1].Hash
        if (-not $equal) { $failures++ }
        [pscustomobject]@{ Path = $path; Run = $run; BaselineSeconds = $measurements[0].Seconds;
            CandidateSeconds = $measurements[1].Seconds; Identical = $equal } | ConvertTo-Json -Compress
    }
}
if ($failures -gt 0) { throw "$failures responses differ from the baseline." }
