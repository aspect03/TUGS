param(
    [string[]]$DotNetArgs = @("run", "--project", ".\IMAJINATION BACKUP.csproj")
)

$repoRoot = Split-Path -Parent $PSScriptRoot
$localAppData = Join-Path $repoRoot ".local-appdata"
$localNuGet = Join-Path $localAppData "NuGet"
$localTemp = Join-Path $repoRoot ".tmp"
$localPackages = Join-Path $repoRoot ".nuget-packages"

$null = New-Item -ItemType Directory -Force -Path $localNuGet, $localTemp, $localPackages

$repoNuGetConfig = Join-Path $repoRoot "NuGet.Config"
if (Test-Path $repoNuGetConfig) {
    Copy-Item $repoNuGetConfig (Join-Path $localNuGet "NuGet.Config") -Force
}

$env:APPDATA = $localAppData
$env:TEMP = $localTemp
$env:TMP = $localTemp
$env:NUGET_PACKAGES = $localPackages

& dotnet @DotNetArgs
exit $LASTEXITCODE
