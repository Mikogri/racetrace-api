$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('racetrace-erasure-test-' + [Guid]::NewGuid().ToString('N'))
$prices = Join-Path $root 'prices'
$forecasts = Join-Path $root 'predictions'
New-Item -ItemType Directory -Path $prices,$forecasts | Out-Null
$price = Join-Path $prices 'snapshot-test.json'
$forecast = Join-Path $forecasts (('A' * 64) + '.json')
$unrelated = Join-Path $forecasts 'keep.txt'
try {
    # Synthetic data only: proves discarded vendor fields cannot survive the whitelist.
    [IO.File]::WriteAllText($price, '{"id":"test","season":2026,"riders":[{"externalId":"1","price":100,"injuryFlag":true,"unexpected":"discard","historicalRounds":[{"points":5,"injury":"discard"}]}]}')
    [IO.File]::WriteAllText($forecast, '{}')
    [IO.File]::WriteAllText($unrelated, 'keep')
    $original = [IO.File]::ReadAllText($price)
    $preview = & "$PSScriptRoot/Remove-LegacyFantasyData.ps1" -PriceDirectory $prices -ForecastDirectory $forecasts
    if ($preview.Applied -or [IO.File]::ReadAllText($price) -cne $original -or -not (Test-Path -LiteralPath $forecast)) { throw 'Preview mutated data.' }
    & "$PSScriptRoot/Remove-LegacyFantasyData.ps1" -PriceDirectory $prices -ForecastDirectory $forecasts -Apply | Out-Null
    $clean = Get-Content -LiteralPath $price -Raw | ConvertFrom-Json -AsHashtable
    if ($clean.dataVersion -ne 1 -or $clean.riders[0].price -ne 100 -or $clean.riders[0].historicalRounds[0].points -ne 5) { throw 'Sports data changed.' }
    if ([IO.File]::ReadAllText($price) -match 'injury|unexpected') { throw 'Legacy data survived.' }
    if ((Test-Path -LiteralPath $forecast) -or -not (Test-Path -LiteralPath $unrelated)) { throw 'Wrong deletion target.' }
    & "$PSScriptRoot/Remove-LegacyFantasyData.ps1" -PriceDirectory $prices -ForecastDirectory $forecasts -Apply | Out-Null
    Write-Output 'PASS: preview, whitelist erasure, unchanged sporting values, scoped deletion, idempotency.'
} finally {
    # Exact test files only; no recursive delete against a computed directory.
    foreach ($file in @($price,$forecast,$unrelated)) { if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file } }
    Remove-Item -LiteralPath $prices,$forecasts,$root
}
