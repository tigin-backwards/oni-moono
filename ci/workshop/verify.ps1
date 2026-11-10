# Checks a Workshop item folder against Moono's GitHub builds (docs/RELEASES.md). Run the copy
# from GitHub, not one inside the item: whoever controls the item can change that one.
#   gh api -H 'Accept: application/vnd.github.raw' repos/tigin-backwards/oni-moono/contents/ci/workshop/verify.ps1 > verify.ps1
#   powershell -ExecutionPolicy Bypass -File verify.ps1 ITEM-FOLDER
# 1. Each version's release manifest (oni-moono-*.SHA256SUMS) by its SLSA provenance, from
#    release.yml on its branch (u60 for the root, master for archived_versions\u59), and every file
#    of its player against it.
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

$versions = @($here) + @(Get-ChildItem -Directory -LiteralPath (Join-Path $here 'archived_versions') -ErrorAction SilentlyContinue | ForEach-Object FullName)
foreach ($version in $versions) {
    if (-not (Test-Path (Join-Path $version 'mod_info.yaml'))) { continue }
    $branch = if ($version -eq $here) { 'u60' } else { 'master' }
    foreach ($manifest in Get-ChildItem -LiteralPath $version -Filter 'oni-moono-*.SHA256SUMS') {
        $platform = if ($manifest.Name -like '*-windows-x64.SHA256SUMS') { 'windows-x64' } elseif ($manifest.Name -like '*-linux-x64.SHA256SUMS') { 'linux-x64' } else { throw "unexpected $($manifest.Name)" }
        if (-not (Attested $manifest.FullName 'release.yml' $branch)) { throw "$($manifest.Name): not attested as a release.yml build of $branch" }
        Check $manifest.FullName (Join-Path $version $platform)
        Write-Host "$version\${platform}: as the attested $($manifest.Name) ($branch)"
    }
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
