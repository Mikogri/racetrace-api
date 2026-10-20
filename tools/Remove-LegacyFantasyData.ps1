# Run with all API/import/background writers stopped. Preview is the default.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PriceDirectory,
    [Parameter(Mandatory)][string]$ForecastDirectory,
    [switch]$Apply
)
$ErrorActionPreference = 'Stop'

function Resolve-DataDirectory([string]$Path) {
    if (-not [IO.Path]::IsPathFullyQualified($Path)) { throw 'Use an explicit absolute data directory.' }
    $resolved = (Resolve-Path -LiteralPath $Path).ProviderPath.TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ($resolved -eq [IO.Path]::GetPathRoot($resolved).TrimEnd([IO.Path]::DirectorySeparatorChar)) { throw 'A filesystem root is not a data directory.' }
    if (Test-Path -LiteralPath (Join-Path $resolved '.git')) { throw 'A repository root is not a data directory.' }
    if ((Get-Item -LiteralPath $resolved).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked directories are not supported.' }
    return $resolved
}

function Select-Fields($Object, [string[]]$Fields) {
    $result = [ordered]@{}
    foreach ($field in $Fields) { if ($Object.Contains($field)) { $result[$field] = $Object[$field] } }
    return $result
}

$prices = Resolve-DataDirectory $PriceDirectory
$forecasts = Resolve-DataDirectory $ForecastDirectory
if ($prices -eq $forecasts) { throw 'Price and forecast directories must be distinct.' }
$files = @(Get-ChildItem -LiteralPath $prices -File -Recurse -Filter 'snapshot-*.json')
foreach ($file in $files) {
    if (-not $file.FullName.StartsWith($prices + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'File is outside the validated data directory.' }
    $source = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json -AsHashtable
    if (-not $source.ContainsKey('riders') -or -not $source.ContainsKey('season')) { throw 'Invalid price snapshot; nothing was overwritten for this file.' }
    $clean = Select-Fields $source @('id','season','upcomingRoundId','fetchedAtUtc','sourceUrl','acquisitionMethod')
    $clean['riders'] = @($source.riders | ForEach-Object {
        $rider = Select-Fields $_ @('externalId','riderId','name','gender','price','seasonPoints','fetchedAtUtc','mappingStatus','countryName')
        $rider['historicalRounds'] = @($_.historicalRounds | ForEach-Object { Select-Fields $_ @('pinkbikeRoundNumber','name','date','price','points') })
        $rider
    })
    $clean['dataVersion'] = 1
    if ($Apply) {
        $temporary = $file.FullName + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
        try {
            $clean | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $temporary -Encoding utf8NoBOM
            Move-Item -LiteralPath $temporary -Destination $file.FullName -Force
        } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
    }
}
$derived = @(Get-ChildItem -LiteralPath $forecasts -File | Where-Object { $_.Name -match '^[A-Fa-f0-9]{64}\.json$' })
foreach ($file in $derived) {
    if (-not $file.FullName.StartsWith($forecasts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Forecast is outside the validated directory.' }
    if ($Apply) { Remove-Item -LiteralPath $file.FullName }
}
[pscustomobject]@{ Applied = [bool]$Apply; PriceSnapshots = $files.Count; ForecastsToRegenerate = $derived.Count }
