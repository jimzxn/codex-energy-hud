param([switch]$Publish, [string]$PublishDirectory = 'artifacts/release/CodexHud')
$ErrorActionPreference = 'Stop'
$widgetRoot = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $widgetRoot '.cache\dotnet'
$env:NUGET_PACKAGES = Join-Path $widgetRoot '.cache\nuget'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$widgetDotnet = Join-Path $widgetRoot '.tools\dotnet\dotnet.exe'
$env:DOTNET_ROOT = Join-Path $widgetRoot '.tools\dotnet'
if (-not (Test-Path -LiteralPath $widgetDotnet)) { & node (Join-Path $PSScriptRoot 'install-sdk.mjs'); if ($LASTEXITCODE) { throw 'SDK installation failed' } }
Push-Location $widgetRoot
try {
    & $widgetDotnet build src/CodexHud/CodexHud.csproj -c Release --configfile NuGet.Config
    if ($LASTEXITCODE) { throw 'Build failed' }
    & $widgetDotnet run --project tests/CodexHud.Tests.csproj -c Release
    if ($LASTEXITCODE) { throw 'Tests failed' }
    if ($Publish) {
        & $widgetDotnet publish src/CodexHud/CodexHud.csproj -c Release -r win-x64 --self-contained true -o $PublishDirectory --configfile NuGet.Config -p:PublishSingleFile=false
        if ($LASTEXITCODE) { throw 'Publish failed' }
    }
} finally { Pop-Location }
