param([switch]$RequireComplete, [string]$ReleaseDirectory = 'artifacts/release/CodexHud', [string]$EvidenceDirectory = 'artifacts/validation/soak-release')
$ErrorActionPreference = 'Stop'
$widgetRoot = Split-Path -Parent $PSScriptRoot
$widgetSoak = if ([System.IO.Path]::IsPathRooted($EvidenceDirectory)) { $EvidenceDirectory } else { Join-Path $widgetRoot $EvidenceDirectory }
$widgetLaunch = Get-Content -LiteralPath (Join-Path $widgetSoak 'launch.json') -Raw | ConvertFrom-Json
$widgetReceipt = Get-Content -LiteralPath (Join-Path $widgetSoak 'soak-result.json') -Raw | ConvertFrom-Json
$widgetRelease = if ([System.IO.Path]::IsPathRooted($ReleaseDirectory)) { $ReleaseDirectory } else { Join-Path $widgetRoot $ReleaseDirectory }
$widgetHashMatch = (Get-FileHash -LiteralPath (Join-Path $widgetRelease 'CodexHud.exe') -Algorithm SHA256).Hash -eq $widgetLaunch.exeSha256 -and
    (Get-FileHash -LiteralPath (Join-Path $widgetRelease 'CodexHud.dll') -Algorithm SHA256).Hash -eq $widgetLaunch.uiDllSha256 -and
    (Get-FileHash -LiteralPath (Join-Path $widgetRelease 'CodexHud.Core.dll') -Algorithm SHA256).Hash -eq $widgetLaunch.coreDllSha256
$widgetProcess = Get-Process -Id $widgetLaunch.pid -ErrorAction SilentlyContinue
$widgetIdentity = 'not-running'
if ($null -ne $widgetProcess) {
    # PowerShell versions differ: ConvertFrom-Json may already return DateTime values.
    $widgetExpectedTime = if ($widgetLaunch.processStartTime -is [datetime]) { $widgetLaunch.processStartTime.ToUniversalTime() } else { ([datetimeoffset]::Parse($widgetLaunch.processStartTime)).UtcDateTime }
    $widgetIdentity = if ($widgetProcess.Path -eq $widgetLaunch.executable -and $widgetProcess.StartTime.ToUniversalTime().Ticks -eq $widgetExpectedTime.Ticks) { 'matched' } else { 'mismatch' }
}
$widgetCompleted = $widgetHashMatch -and $widgetReceipt.status -eq 'completed' -and $widgetReceipt.completed -eq $true -and $widgetReceipt.requestedSeconds -eq 7200 -and $widgetReceipt.effectiveCoverageSeconds -ge 7200
$widgetRequestedCompleted = $widgetHashMatch -and $widgetReceipt.status -eq 'completed' -and $widgetReceipt.completed -eq $true -and $widgetReceipt.requestedSeconds -gt 0 -and $widgetReceipt.effectiveCoverageSeconds -ge $widgetReceipt.requestedSeconds
$widgetFreshness = @{}
if (Test-Path -LiteralPath (Join-Path $widgetSoak 'resources.jsonl')) {
    $widgetLastLine = Get-Content -LiteralPath (Join-Path $widgetSoak 'resources.jsonl') -Tail 1
    if ($widgetLastLine) {
        $widgetSample = $widgetLastLine | ConvertFrom-Json
        foreach ($widgetMetric in @('quota','hardware','activity')) {
            $widgetFreshness[$widgetMetric] = @{ health=$widgetSample.health.$widgetMetric; observedAt=$widgetSample.health.($widgetMetric+'ObservedAt') }
        }
    }
}
[ordered]@{
    binaryHashesMatch=$widgetHashMatch
    liveProcessIdentity=$widgetIdentity
    status=$widgetReceipt.status
    completedTwoHours=$widgetCompleted
    completedRequestedRun=$widgetRequestedCompleted
    requestedSeconds=$widgetReceipt.requestedSeconds
    effectiveCoverageSeconds=$widgetReceipt.effectiveCoverageSeconds
    averageCpuPercent=$widgetReceipt.averageCpuPercent
    cpuTargetMet=($widgetCompleted -and $widgetReceipt.averageCpuPercent -lt 0.5)
    sampledRunCpuTargetMet=($widgetRequestedCompleted -and $widgetReceipt.averageCpuPercent -lt 0.5)
    skippedIntervals=$widgetReceipt.skippedIntervals
    processReadErrors=$widgetReceipt.processReadErrors
    lastSampleHealth=$widgetFreshness
} | ConvertTo-Json -Depth 6
if (-not $widgetHashMatch -or $widgetIdentity -eq 'mismatch') { exit 1 }
if ($RequireComplete -and -not $widgetCompleted) { exit 2 }
