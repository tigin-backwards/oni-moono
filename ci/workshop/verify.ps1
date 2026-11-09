# Checks this folder against Moono's attested builds (docs/RELEASES.md): each version's release
# manifest (oni-moono-*.SHA256SUMS) by its SLSA provenance and every file of its player against
# it, then this folder's own SHA256SUMS by its provenance and every file against it, and that no
# file is here that SHA256SUMS doesn't list. Needs the GitHub CLI (gh).
#   powershell -ExecutionPolicy Bypass -File verify.ps1
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$repo = 'tigin-backwards/oni-moono'
$signer = "$repo/.github/workflows/attest.yml"

function Attested($file) {
    & gh attestation verify $file -R $repo --signer-workflow $signer | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "$file has no valid attestation" }
}
function Check($manifest, $folder) {
    foreach ($line in Get-Content -Encoding UTF8 -LiteralPath $manifest) {
        if (-not $line) { continue }
        $hash = $line.Substring(0, 64); $path = $line.Substring(66)
        $file = Join-Path $folder ($path -replace '/', '\')
        if (-not (Test-Path -LiteralPath $file)) { throw "$file is missing" }
        if ((Get-FileHash -Algorithm SHA256 -LiteralPath $file).Hash.ToLowerInvariant() -ne $hash) { throw "$file doesn't match $manifest" }
    }
}

$versions = @($here) + @(Get-ChildItem -Directory -LiteralPath (Join-Path $here 'archived_versions') -ErrorAction SilentlyContinue | ForEach-Object FullName)
foreach ($version in $versions) {
    if (-not (Test-Path (Join-Path $version 'mod_info.yaml'))) { continue }
    foreach ($manifest in Get-ChildItem -LiteralPath $version -Filter 'oni-moono-*.SHA256SUMS') {
        $platform = if ($manifest.Name -like '*-windows-x64.SHA256SUMS') { 'windows-x64' } elseif ($manifest.Name -like '*-linux-x64.SHA256SUMS') { 'linux-x64' } else { throw "unexpected $($manifest.Name)" }
        Attested $manifest.FullName
        Check $manifest.FullName (Join-Path $version $platform)
        Write-Host "$version\${platform}: as the attested $($manifest.Name)"
    }
}
$sums = Join-Path $here 'SHA256SUMS'
Attested $sums
Check $sums $here
$listed = Get-Content -Encoding UTF8 -LiteralPath $sums | Where-Object { $_ } | ForEach-Object { $_.Substring(66) } | Sort-Object
$present = Get-ChildItem -Recurse -File -LiteralPath $here | ForEach-Object { $_.FullName.Substring($here.Length + 1) -replace '\\', '/' } | Where-Object { $_ -ne 'SHA256SUMS' } | Sort-Object
if (Compare-Object $listed $present) { throw "files here that SHA256SUMS doesn't list, or the other way round" }
Write-Host "All $($listed.Count) files are as attested."
