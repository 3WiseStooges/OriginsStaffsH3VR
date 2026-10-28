<#
.SYNOPSIS
    Builds the Thunderstore package and publishes it.

.DESCRIPTION
    1. Checks that manifest.json and thunderstore.toml agree on the version, and that this
       version is not on Thunderstore yet (it rejects re-publishing a version).
    2. Runs Unity headless: Origins Staffs > Build Staffs, then MeatKit zips the package into
       Unity/AssetBundles/OriginsStaffs/<version>/.
    3. Publishes that zip with tcli, using the token in $env:THUNDERSTORE_TOKEN (never stored).

    Needs your own BO2 rips in place (see DEVELOPMENT.md), Unity and H3VR closed, and tcli
    (dotnet tool install -g tcli).

.EXAMPLE
    $env:THUNDERSTORE_TOKEN = "<your service account token>"
    ./tools/publish.ps1

.EXAMPLE
    ./tools/publish.ps1 -BuildOnly     # build and check the zip, publish nothing
#>
param(
    [switch]$BuildOnly,
    [string]$Unity = "X:\Tools\Unity\5.6.7f1\Editor\Unity.exe"
)
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

# --- version
$manifest = Get-Content (Join-Path $repo "manifest.json") -Raw | ConvertFrom-Json
$version = $manifest.version_number
$toml = Get-Content (Join-Path $repo "thunderstore.toml") -Raw
$tomlVersion = [regex]::Match($toml, 'versionNumber\s*=\s*"([^"]+)"').Groups[1].Value
$namespace = [regex]::Match($toml, 'namespace\s*=\s*"([^"]+)"').Groups[1].Value
$name = $manifest.name
if ($tomlVersion -ne $version) {
    throw "manifest.json says $version but thunderstore.toml says $tomlVersion. Bump both."
}
Write-Host "Package $namespace-$name $version"

if (-not $BuildOnly) {
    if (-not $env:THUNDERSTORE_TOKEN) { throw 'Set $env:THUNDERSTORE_TOKEN first.' }
    $published = $true
    try {
        Invoke-RestMethod "https://thunderstore.io/api/experimental/package/$namespace/$name/$version/" | Out-Null
    } catch {
        if ($_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq 404) { $published = $false } else { throw }
    }
    if ($published) { throw "$version is already on Thunderstore. Bump version_number in manifest.json and thunderstore.toml." }
}

# --- build
foreach ($p in "Unity", "h3vr") {
    if (Get-Process -Name $p -ErrorAction SilentlyContinue) { throw "Close $p first: the build needs the project to itself." }
}
$zip = Join-Path $repo "Unity\AssetBundles\$name\$version\$namespace-$name-$version.zip"
$log = Join-Path $repo "build\unity-publish.log"
New-Item -ItemType Directory -Force (Split-Path $log) | Out-Null
if (Test-Path $zip) { Remove-Item $zip }
Write-Host "Building with Unity (a few minutes; log: $log)"
& $Unity -batchmode -quit -projectPath (Join-Path $repo "Unity") `
    -executeMethod OriginsStaffs.EditorTools.StaffAssemblyBuilder.BuildThunderstorePackage -logFile $log | Out-Null
# Unity sometimes crashes while shutting down after MeatKit finishes, so judge the build by its output.
if (-not (Test-Path $zip)) { throw "No package was built; see $log." }
if (Select-String -Path $log -Pattern "Build failed|error CS" -Quiet) { throw "The build reported errors; see $log." }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$entries = [IO.Compression.ZipFile]::OpenRead($zip).Entries | ForEach-Object { $_.FullName }
foreach ($required in "manifest.json", "icon.png", "README.md", "OriginsStaffs.dll", "origins_staffs", "late_origins_staffs") {
    if ($entries -notcontains $required) { throw "The package is missing $required." }
}
Write-Host "Built $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB): $($entries -join ', ')"
if ($BuildOnly) { return }

# --- publish
Write-Host "Publishing to Thunderstore"
& tcli publish --file $zip --token $env:THUNDERSTORE_TOKEN --package-namespace $namespace --package-name $name --package-version $version
if ($LASTEXITCODE -ne 0) { throw "tcli publish failed." }
Write-Host "Published $namespace-$name $version"
