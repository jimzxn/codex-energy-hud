$ErrorActionPreference = 'Stop'
$widgetRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$widgetArtifacts = Join-Path $widgetRoot 'artifacts'
$widgetRelease = Join-Path $widgetArtifacts 'release\CodexHud'
if (-not (Test-Path -LiteralPath (Join-Path $widgetRelease 'CodexHud.exe'))) { throw 'Build the release first: ./scripts/build.ps1 -Publish' }

# This command never builds or replaces running binaries. It packages the verified release.
Copy-Item -LiteralPath (Join-Path $widgetRoot 'README.md') -Destination (Join-Path $widgetRelease 'README.md') -Force
$widgetReleaseDocs = Join-Path $widgetRelease 'docs'
New-Item -ItemType Directory -Path $widgetReleaseDocs -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $widgetRoot 'docs') -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $widgetReleaseDocs $_.Name) -Force }
$widgetEvidence = @('ui-final-verified\ui-check.json','home-persistence.json','lifecycle\soak-result.json','hardware.json','hardware-initial-attempt.json','unit-tests.txt','soak-release\launch.json','soak-release\soak-result.json','soak-release\resources.jsonl','soak-initial-build\launch.json','soak-initial-build\soak-result.json')
$widgetEvidence += @('v1.1.0\build-tests.txt','v1.1.0\disk-live.json','v1.1.0\disk-tests.txt','v1.1.0\ui\ui-check.json','v1.1.0\ui-user\ui-check.json','v1.1.0\smoke\launch.json','v1.1.0\smoke\soak-result.json','v1.1.0\smoke\resources.jsonl')
$widgetEvidence += @('v1.2.0\build-tests.txt','v1.2.0\tokens-live.json','v1.2.0\ui-first\ui-check.json','v1.2.0\ui-final\ui-check.json','v1.2.0\process-association.json','v1.2.0\process-association-host.json','v1.2.0\smoke\launch.json','v1.2.0\smoke\soak-result.json','v1.2.0\smoke\resources.jsonl','v1.2.0\smoke\verify-release.json')
$widgetEvidence += @('v1.3.0\build-first.txt','v1.3.0\build-tests.txt','v1.3.0\ledger-live.json','v1.3.0\ui-final\ui-check.json','v1.3.0\smoke\launch.json','v1.3.0\smoke\soak-result.json','v1.3.0\smoke\resources.jsonl','v1.3.0\smoke\verify-release.json')
$widgetEvidence += @('v1.4.0\build-tests.txt','v1.4.0\ui-release\ui-check.json','v1.4.0\smoke-final\launch.json','v1.4.0\smoke-final\soak-result.json','v1.4.0\smoke-final\resources.jsonl','v1.4.0\smoke-final\verify-release.json')
$widgetEvidence += @('v1.4.1\build-tests.txt','v1.4.1\ledger-live.json','v1.4.1\ui-release\ui-check.json','v1.4.1\smoke\launch.json','v1.4.1\smoke\soak-result.json','v1.4.1\smoke\resources.jsonl','v1.4.1\smoke\verify-release.json')
foreach ($widgetRelative in $widgetEvidence) {
    $widgetSource = Join-Path (Join-Path $widgetArtifacts 'validation') $widgetRelative
    if (Test-Path -LiteralPath $widgetSource) {
        $widgetDestination = Join-Path (Join-Path $widgetRelease 'artifacts\validation') $widgetRelative
        New-Item -ItemType Directory -Path (Split-Path -Parent $widgetDestination) -Force | Out-Null
        Copy-Item -LiteralPath $widgetSource -Destination $widgetDestination -Force
    }
}
$widgetBinaryFiles = Get-ChildItem -LiteralPath $widgetRelease -File | Where-Object { $_.Extension -in @('.exe','.dll','.json') -and $_.Name -ne 'release-manifest.json' } | Sort-Object Name
$widgetVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $widgetRelease 'CodexHud.exe')).ProductVersion
$widgetManifest = [ordered]@{ version=$widgetVersion; packagedAtUtc=[DateTimeOffset]::UtcNow.ToString('o'); files=@($widgetBinaryFiles | ForEach-Object { [ordered]@{ name=$_.Name; bytes=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } }) }
$widgetManifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $widgetRelease 'release-manifest.json') -Encoding utf8

function Write-WidgetArchive([string]$Destination, [object[]]$Entries) {
    $widgetTemporaryZip = $Destination + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    $widgetZip = [System.IO.Compression.ZipFile]::Open($widgetTemporaryZip, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($widgetEntry in $Entries) {
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($widgetZip, $widgetEntry.Path, $widgetEntry.Name.Replace('\','/'), [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $widgetZip.Dispose() }
    Move-Item -LiteralPath $widgetTemporaryZip -Destination $Destination -Force
}
$widgetReleaseEntries = @(Get-ChildItem -LiteralPath $widgetRelease -File -Recurse | ForEach-Object { @{Path=$_.FullName; Name=[System.IO.Path]::GetRelativePath($widgetRelease,$_.FullName)} })
Write-WidgetArchive (Join-Path $widgetArtifacts 'CodexHud-win-x64.zip') $widgetReleaseEntries

$widgetSourceEntries = [System.Collections.Generic.List[object]]::new()
foreach ($widgetFolder in @('src','scripts','tests','docs')) {
    Get-ChildItem -LiteralPath (Join-Path $widgetRoot $widgetFolder) -File -Recurse | Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } | ForEach-Object {
        $widgetSourceEntries.Add(@{Path=$_.FullName; Name=[System.IO.Path]::GetRelativePath($widgetRoot,$_.FullName)})
    }
}
foreach ($widgetName in @('README.md','Directory.Build.props','global.json','NuGet.Config','.gitignore')) {
    $widgetSourceEntries.Add(@{Path=(Join-Path $widgetRoot $widgetName); Name=$widgetName})
}
foreach ($widgetRelative in $widgetEvidence) {
    # Use the evidence snapshot already copied into the release, not a live writer's file.
    $widgetSource = Join-Path (Join-Path $widgetRelease 'artifacts\validation') $widgetRelative
    if (Test-Path -LiteralPath $widgetSource) { $widgetSourceEntries.Add(@{Path=$widgetSource; Name=('artifacts/validation/' + $widgetRelative.Replace('\','/'))}) }
}
Write-WidgetArchive (Join-Path $widgetArtifacts 'CodexHud-source.zip') $widgetSourceEntries.ToArray()
Get-Item -LiteralPath (Join-Path $widgetArtifacts 'CodexHud-win-x64.zip'),(Join-Path $widgetArtifacts 'CodexHud-source.zip') | Select-Object Name,Length
