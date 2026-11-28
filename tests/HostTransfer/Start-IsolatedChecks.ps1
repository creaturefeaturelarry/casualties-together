param(
    [Parameter(Mandatory=$true)][string]$GamePath,
    [Parameter(Mandatory=$true)][string]$ProfilePath,
    [string]$PrototypeDirectory = 'build/host-transfer/ui-prototype'
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$lab = Join-Path $repo ('build/host-transfer/startup-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$runtime = Join-Path $lab 'runtime'
New-Item -ItemType Directory -Path $runtime -Force | Out-Null
Copy-Item -LiteralPath $ProfilePath -Destination (Join-Path $lab 'profile-before') -Recurse
foreach ($file in @('CasualtiesUnknown.exe','UnityPlayer.dll','winhttp.dll','doorstop_config.ini','.doorstop_version')) {
    Copy-Item -LiteralPath (Join-Path $GamePath $file) -Destination $runtime
}
foreach ($dir in @('CasualtiesUnknown_Data','MonoBleedingEdge')) {
    New-Item -ItemType Junction -Path (Join-Path $runtime $dir) -Target (Join-Path $GamePath $dir) | Out-Null
}
$bep = Join-Path $runtime 'BepInEx'
New-Item -ItemType Directory -Path $bep -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $GamePath 'BepInEx/core') -Destination $bep -Recurse
Copy-Item -LiteralPath (Join-Path $GamePath 'BepInEx/config') -Destination $bep -Recurse
$plugins = Join-Path $bep 'plugins'
New-Item -ItemType Directory -Path $plugins -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repo $PrototypeDirectory) -Destination (Join-Path $plugins 'Together') -Recurse
Copy-Item -LiteralPath (Join-Path $repo 'libs/dlls/CUCoreLib/CUCoreLib.dll') -Destination $plugins
Copy-Item -LiteralPath (Join-Path $repo 'build/artifacts/RuntimeChecks/bin/DebugFullgame/netstandard2.1/Together.HostTransferRuntimeChecks.dll') -Destination $plugins
Set-Content -LiteralPath (Join-Path $runtime 'steam_appid.txt') -Value '4576490'
$reportedSettings = Join-Path $repo 'build/host-transfer/playtest-report-20261118/recipient.log'
if (Test-Path -LiteralPath $reportedSettings) {
    $match = [regex]::Match([IO.File]::ReadAllText($reportedSettings), 'Run settings: ([A-Za-z0-9+/=]+)')
    if ($match.Success) { Set-Content -LiteralPath (Join-Path $runtime 'reported-run-settings.txt') -Value $match.Groups[1].Value }
}
$playerLog = Join-Path $lab 'player.log'
$process = Start-Process -FilePath (Join-Path $runtime 'CasualtiesUnknown.exe') -WorkingDirectory $runtime -WindowStyle Hidden -ArgumentList @('-batchmode','-nographics','-logFile',('"' + $playerLog + '"')) -PassThru
@{ path=$lab; pid=$process.Id; profileBackup=(Join-Path $lab 'profile-before') } | ConvertTo-Json
