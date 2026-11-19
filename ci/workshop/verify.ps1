# Checks a Workshop item folder against Moono's GitHub builds (docs/RELEASES.md). Run the copy
# from GitHub, not one inside the item: whoever controls the item can change that one.
#   gh api -H 'Accept: application/vnd.github.raw' repos/tigin-backwards/oni-moono/contents/ci/workshop/verify.ps1 > verify.ps1
#   powershell -ExecutionPolicy Bypass -File verify.ps1 ITEM-FOLDER
# 1. The release manifests (oni-moono-*.SHA256SUMS) by their SLSA provenance, from release.yml
#    on master (on u60 for an item laid out before the U60 cut-over), and every file of each
#    player against its manifest. The item has one version: an archived_versions folder is
#    refused.
# 2. The item's SHA256SUMS by its provenance from workshop.yml (looked up, and the bundle in
#    attestation\), every file against it, and that no file is here that SHA256SUMS doesn't list.
# Needs the GitHub CLI (gh). Windows PowerShell 5.1 or PowerShell 7.
param([string]$Item = $PSScriptRoot)
$ErrorActionPreference = 'Stop'
$here = (Resolve-Path -LiteralPath $Item).Path.TrimEnd('\')
$repo = 'tigin-backwards/oni-moono'
$signer = "$repo/.github/workflows/attest.yml"

$sums = Join-Path $here 'SHA256SUMS'

# Whether $file's provenance is by attest.yml, called by $workflow on $branch of the repository.
function Attested($file, $workflow, $branch, $bundle) {
    $extra = if ($bundle) { @('--bundle', $bundle) } else { @() }
    $configs = & gh attestation verify $file -R $repo --signer-workflow $signer --source-ref "refs/heads/$branch" @extra --format json --jq '.[].verificationResult.signature.certificate.buildConfigURI' 2>$null
    return $LASTEXITCODE -eq 0 -and @($configs) -ccontains "https://github.com/$repo/.github/workflows/$workflow@refs/heads/$branch"
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

if (Test-Path -LiteralPath (Join-Path $here 'archived_versions')) { throw "archived_versions: the item has one version" }
if (-not (Test-Path -LiteralPath (Join-Path $here 'mod_info.yaml'))) { throw "mod_info.yaml is missing" }
$manifests = @(Get-ChildItem -LiteralPath $here -Filter 'oni-moono-*.SHA256SUMS')
if ($manifests.Count -eq 0) { throw "no release manifests" }
foreach ($manifest in $manifests) {
    $platform = if ($manifest.Name -like '*-windows-x64.SHA256SUMS') { 'windows-x64' } elseif ($manifest.Name -like '*-linux-x64.SHA256SUMS') { 'linux-x64' } else { throw "unexpected $($manifest.Name)" }
    $branch = $null
    foreach ($b in 'master', 'u60') { if (Attested $manifest.FullName 'release.yml' $b) { $branch = $b; break } }
    if (-not $branch) { throw "$($manifest.Name): not attested as a release.yml build of master" }
    Check $manifest.FullName (Join-Path $here $platform)
    Write-Host "${platform}: as the attested $($manifest.Name) ($branch)"
}
$bundle = Join-Path $here 'attestation\SHA256SUMS.sigstore.json'
if (((@(Get-ChildItem -LiteralPath (Join-Path $here 'attestation'))).Name -join ',') -ne 'SHA256SUMS.sigstore.json') { throw "attestation\ should hold SHA256SUMS.sigstore.json alone" }
$itemBranch = $null
foreach ($b in 'master', 'u60') { if ((Attested $sums 'workshop.yml' $b) -and (Attested $sums 'workshop.yml' $b $bundle)) { $itemBranch = $b; break } }
if (-not $itemBranch) { throw "SHA256SUMS: not attested by workshop.yml on master or u60" }
Write-Host "SHA256SUMS: attested by workshop.yml on $itemBranch"
Check $sums $here
$listed = Get-Content -Encoding UTF8 -LiteralPath $sums | Where-Object { $_ } | ForEach-Object { $_.Substring(66) } | Sort-Object
$present = Get-ChildItem -Recurse -File -LiteralPath $here | ForEach-Object { $_.FullName.Substring($here.Length + 1) -replace '\\', '/' } | Where-Object { $_ -ne 'SHA256SUMS' -and $_ -ne 'attestation/SHA256SUMS.sigstore.json' } | Sort-Object
if (Compare-Object $listed $present) { throw "files here that SHA256SUMS doesn't list, or the other way round" }
Write-Host "All $($listed.Count) files are as attested."
