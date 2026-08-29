param(
    [Parameter(Mandatory = $true)]
    [string]$GameRoot,
    [int]$TargetChapter = 3,
    [int]$TimeoutMinutes = 45,
    [int]$StatusIntervalSeconds = 1,
    [string]$ProfileName = "codex-qa",
    [string]$SeedProfileRoot = "",
    [switch]$ContinueExistingProfile
)

$ErrorActionPreference = "Stop"
$script:QaArtifactRoot = ""
trap {
    if (-not [string]::IsNullOrWhiteSpace($script:QaArtifactRoot)) {
        $detail = ($_ | Format-List * -Force | Out-String)
        Set-Content -LiteralPath (Join-Path $script:QaArtifactRoot "qa-script-error.txt") -Value $detail -Encoding UTF8
    }
    exit 99
}

function New-RandomHex([int]$ByteCount) {
    $bytes = New-Object byte[] $ByteCount
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    return ([System.BitConverter]::ToString($bytes)).Replace("-", "").ToLowerInvariant()
}

function Get-Sha256Text([string]$Value) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($Value)
        return ([System.BitConverter]::ToString($sha.ComputeHash($bytes))).Replace("-", "").ToLowerInvariant()
    }
    finally { $sha.Dispose() }
}

function Send-PipeRequest(
    [string]$PipeName,
    [string]$Token,
    [int]$ProcessId,
    [string]$ProcessInstanceId,
    [string]$Command,
    $Options,
    [string]$RequestId = ""
) {
    if ([string]::IsNullOrWhiteSpace($RequestId)) {
        $RequestId = [Guid]::NewGuid().ToString("N")
    }

    $request = [ordered]@{
        id = $RequestId
        token = $Token
        targetGameProcessId = $ProcessId
        targetProcessInstanceId = $ProcessInstanceId
        command = $Command
        options = $Options
        arguments = $null
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    do {
        $pipe = New-Object System.IO.Pipes.NamedPipeClientStream(
            ".",
            $PipeName,
            [System.IO.Pipes.PipeDirection]::InOut)
        try {
            $pipe.Connect(3000)
            $writer = New-Object System.IO.StreamWriter(
                $pipe,
                (New-Object System.Text.UTF8Encoding($false)),
                1024,
                $true)
            $reader = New-Object System.IO.StreamReader(
                $pipe,
                [System.Text.Encoding]::UTF8,
                $false,
                1024,
                $true)
            try {
                $writer.AutoFlush = $true
                $writer.WriteLine(($request | ConvertTo-Json -Compress -Depth 32))
                $line = $reader.ReadLine()
                if ([string]::IsNullOrWhiteSpace($line)) {
                    throw "Plugin pipe returned an empty response."
                }
                $response = $line | ConvertFrom-Json
            }
            finally {
                $reader.Dispose()
                $writer.Dispose()
            }
        }
        finally { $pipe.Dispose() }

        if (-not ($response.Data -and $response.Data.pending -eq $true)) {
            return $response
        }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Timed out waiting for the plugin to complete command '$Command'."
}

function Save-ProcessWindowScreenshot([System.Diagnostics.Process]$Process, [string]$Path) {
    if (-not ("AutoPlayerQa.NativeWindow" -as [type])) {
        Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
namespace AutoPlayerQa {
    public static class NativeWindow {
        [StructLayout(LayoutKind.Sequential)]
        public struct Rect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr handle, out Rect rect);
    }
}
"@
    }

    $Process.Refresh()
    $handle = $Process.MainWindowHandle
    if ($handle -eq [IntPtr]::Zero) { return $false }
    $rect = New-Object AutoPlayerQa.NativeWindow+Rect
    if (-not [AutoPlayerQa.NativeWindow]::GetWindowRect($handle, [ref]$rect)) { return $false }
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    if ($width -le 0 -or $height -le 0) { return $false }

    Add-Type -AssemblyName System.Drawing
    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
    return $true
}

$resolvedGameRoot = (Resolve-Path -LiteralPath $GameRoot).Path.TrimEnd('\', '/')
$executable = Get-ChildItem -LiteralPath $resolvedGameRoot -Filter "*.exe" -File |
    Where-Object { $_.Name -notmatch "UnityCrashHandler|AutoPlayer" } |
    Select-Object -First 1
if (-not $executable) { throw "No game executable was found under '$resolvedGameRoot'." }

$dataDirectory = Join-Path $resolvedGameRoot ($executable.BaseName + "_Data")
$assemblyPath = Join-Path $dataDirectory "Managed\Assembly-CSharp.dll"
if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) {
    throw "Assembly-CSharp.dll was not found at '$assemblyPath'."
}

$normalizedRoot = [System.IO.Path]::GetFullPath($resolvedGameRoot).TrimEnd('\', '/').ToUpperInvariant()
$gameId = (Get-Sha256Text $normalizedRoot).Substring(0, 16)
$sessionId = [DateTime]::UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + (New-RandomHex 4)
$dataRoot = Join-Path $env:LOCALAPPDATA "LoopstructorAutoPlayer"
$safeProfile = ($ProfileName -replace '[^a-zA-Z0-9._-]', '_') + "-" + (New-RandomHex 4)
$profileRoot = Join-Path (Join-Path (Join-Path $dataRoot "profiles") $gameId) $safeProfile
$artifactRoot = Join-Path (Join-Path (Join-Path $dataRoot "artifacts") $gameId) $sessionId
[System.IO.Directory]::CreateDirectory($profileRoot) | Out-Null
[System.IO.Directory]::CreateDirectory($artifactRoot) | Out-Null
$script:QaArtifactRoot = $artifactRoot
$resolvedSeedProfile = ""

if (-not [string]::IsNullOrWhiteSpace($SeedProfileRoot)) {
    $resolvedSeedProfile = (Resolve-Path -LiteralPath $SeedProfileRoot).Path.TrimEnd('\', '/')
    $profilesRoot = [System.IO.Path]::GetFullPath((Join-Path $dataRoot "profiles")).TrimEnd('\', '/')
    if (-not $resolvedSeedProfile.StartsWith($profilesRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Seed profile must be an existing isolated QA profile under '$profilesRoot'."
    }
    if ([StringComparer]::OrdinalIgnoreCase.Equals($resolvedSeedProfile, $profileRoot)) {
        throw "Seed profile and destination profile must be different."
    }
    if (Get-ChildItem -LiteralPath $resolvedSeedProfile -Recurse -Force | Where-Object {
            $_.Attributes -band [System.IO.FileAttributes]::ReparsePoint
        } | Select-Object -First 1) {
        throw "Seed profile contains a reparse point and cannot be copied safely."
    }

    Get-ChildItem -LiteralPath $resolvedSeedProfile -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $profileRoot -Recurse -Force
    }
}

$pipeName = "Loopstructor.AutoPlayer." + (New-RandomHex 8)
$token = New-RandomHex 32
$assemblySha256 = (Get-FileHash -LiteralPath $assemblyPath -Algorithm SHA256).Hash.ToLowerInvariant()
$logPath = Join-Path $artifactRoot "Player.log"

$startInfo = New-Object System.Diagnostics.ProcessStartInfo
$startInfo.FileName = $executable.FullName
$startInfo.WorkingDirectory = $resolvedGameRoot
$startInfo.UseShellExecute = $false
$startInfo.Arguments = '-logFile "' + $logPath + '"'
$environment = $startInfo.EnvironmentVariables
$environment["SteamAppId"] = "3841840"
$environment["SteamGameId"] = "3841840"
$environment["LOOPSTRUCTOR_AUTOPLAYER_ENABLED"] = "1"
$environment["LOOPSTRUCTOR_AUTOPLAYER_PIPE"] = $pipeName
$environment["LOOPSTRUCTOR_AUTOPLAYER_TOKEN"] = $token
$environment["LOOPSTRUCTOR_AUTOPLAYER_PROFILE_ROOT"] = $profileRoot
$environment["LOOPSTRUCTOR_AUTOPLAYER_ARTIFACT_ROOT"] = $artifactRoot
$environment["LOOPSTRUCTOR_AUTOPLAYER_ASSEMBLY_SHA256"] = $assemblySha256
$environment["LOOPSTRUCTOR_AUTOPLAYER_CHEAT_ALLOWED"] = "1"

$process = [System.Diagnostics.Process]::Start($startInfo)
if (-not $process) { throw "Windows did not create the game process." }

$metadata = [ordered]@{
    sessionId = $sessionId
    processId = $process.Id
    gameRoot = $resolvedGameRoot
    profileRoot = $profileRoot
    artifactRoot = $artifactRoot
    pipeName = $pipeName
    targetChapter = $TargetChapter
    seedProfileRoot = $resolvedSeedProfile
    continueExistingProfile = [bool]$ContinueExistingProfile
    startedAtUtc = [DateTime]::UtcNow
}
$metadata | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $artifactRoot "qa-run.json") -Encoding UTF8
Write-Output ("QA_PROCESS_ID={0}" -f $process.Id)
Write-Output ("QA_ARTIFACT_ROOT={0}" -f $artifactRoot)
Write-Output ("QA_PROFILE_ROOT={0}" -f $profileRoot)

$hello = $null
$lastHelloError = ""
$helloDeadline = [DateTime]::UtcNow.AddSeconds(90)
while (-not $hello -and [DateTime]::UtcNow -lt $helloDeadline) {
    if ($process.HasExited) { throw "The game exited before the plugin handshake (exit code $($process.ExitCode))." }
    try {
        $candidate = Send-PipeRequest $pipeName $token $process.Id "" "hello" $null
        if ($candidate.Success -and $candidate.Hello) { $hello = $candidate }
    }
    catch {
        $lastHelloError = $_.Exception.Message
        Set-Content -LiteralPath (Join-Path $artifactRoot "qa-handshake-error.txt") -Value $lastHelloError -Encoding UTF8
        Start-Sleep -Seconds 1
    }
}
if (-not $hello) {
    throw "The plugin handshake did not become available within 90 seconds. Last error: $lastHelloError"
}

$processInstanceId = [string]$hello.Hello.ProcessInstanceId
if ([string]::IsNullOrWhiteSpace($processInstanceId)) { throw "The plugin handshake omitted ProcessInstanceId." }
Write-Output ("QA_PLUGIN_VERSION={0}" -f $hello.Hello.PluginVersion)
Write-Output ("QA_PROCESS_INSTANCE_ID={0}" -f $processInstanceId)

$options = [ordered]@{
    mode = 0
    characterIndex = 0
    difficultyIndex = 0
    superModuleIndex = 0
    randomVehicleIndex = 0
    randomFetterIndex = 0
    gameSpeedControlVersion = 1
    overrideGameSpeed = $true
    speedState = 0
    maxRunMinutes = [Math]::Max($TimeoutMinutes, 5)
    maxWaves = 0
    continueExistingProfile = [bool]$ContinueExistingProfile
}
$start = $null
$startDeadline = [DateTime]::UtcNow.AddSeconds(90)
do {
    $candidate = Send-PipeRequest $pipeName $token $process.Id $processInstanceId "start" $options
    if ($candidate.Success) {
        $start = $candidate
        break
    }

    if ($candidate.Status -and $candidate.Status.NeedsProcessRestart -eq $true) {
        throw "The plugin requires a process restart before start can be retried: $($candidate.Message)"
    }
    Start-Sleep -Seconds 1
} while ([DateTime]::UtcNow -lt $startDeadline)
if (-not $start) { throw "The plugin did not accept start within 90 seconds of the handshake." }
Write-Output "QA_AUTOPLAY_STARTED=true"

$deadline = [DateTime]::UtcNow.AddMinutes($TimeoutMinutes)
$lastSignature = ""
$status = $start.Status
while ([DateTime]::UtcNow -lt $deadline) {
    if ($process.HasExited) { throw "The game exited during auto-play (exit code $($process.ExitCode))." }
    $response = Send-PipeRequest $pipeName $token $process.Id $processInstanceId "status" $null
    if (-not $response.Success -or -not $response.Status) { throw "Status failed: $($response.Message)" }
    $status = $response.Status
    $signature = "{0}|{1}|{2}|{3}|{4}|{5}|{6}" -f `
        $status.RunState, $status.Stage, $status.CurrentChapter, $status.CurrentMapLayer,
        $status.WavesStarted, $status.WavesCompleted, $status.LastCommand
    if ($signature -ne $lastSignature) {
        $lastSignature = $signature
        Write-Output ("QA_STATUS={0:o}|state={1}|stage={2}|chapter={3}|mapStage={4}|layer={5}|waves={6}/{7}|command={8}|lastMs={9}|maxMs={10}|slow={11}|detail={12}" -f `
            [DateTime]::UtcNow, $status.RunState, $status.Stage, $status.CurrentChapter,
            $status.CurrentMapStage, $status.CurrentMapLayer, $status.WavesCompleted,
            $status.WavesStarted, $status.LastRuntimeCommand,
            $status.LastRuntimeCommandDurationMs, $status.MaxRuntimeCommandDurationMs,
            $status.SlowRuntimeCommandCount, $status.StageDetail)
    }

    if ([int]$status.CurrentChapter -ge $TargetChapter) {
        $finalPath = Join-Path $artifactRoot "qa-target-status.json"
        $status | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $finalPath -Encoding UTF8
        $screenshotPath = Join-Path $artifactRoot "qa-target-screen.png"
        $captured = Save-ProcessWindowScreenshot $process $screenshotPath
        $pause = Send-PipeRequest $pipeName $token $process.Id $processInstanceId "pause" $null
        if (-not $pause.Success) {
            $stop = Send-PipeRequest $pipeName $token $process.Id $processInstanceId "stop" $null
            Write-Output ("QA_TARGET_REACHED=true;CHAPTER={0};MAP_STAGE={1};LAYER={2};SCREENSHOT={3};CAPTURED={4};PAUSED=false;STOPPED={5};PAUSE_ERROR={6}" -f `
                $status.CurrentChapter, $status.CurrentMapStage, $status.CurrentMapLayer,
                $screenshotPath, $captured, $stop.Success, $pause.Message)
            exit 4
        }
        Write-Output ("QA_TARGET_REACHED=true;CHAPTER={0};MAP_STAGE={1};LAYER={2};SCREENSHOT={3};CAPTURED={4};PAUSED={5}" -f `
            $status.CurrentChapter, $status.CurrentMapStage, $status.CurrentMapLayer,
            $screenshotPath, $captured, $pause.Success)
        exit 0
    }

    if ([int]$status.RunState -in @(3, 4, 5)) {
        $finalPath = Join-Path $artifactRoot "qa-terminal-status.json"
        $status | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $finalPath -Encoding UTF8
        $screenshotPath = Join-Path $artifactRoot "qa-terminal-screen.png"
        $captured = Save-ProcessWindowScreenshot $process $screenshotPath
        Write-Output ("QA_TERMINAL=true;STATE={0};CHAPTER={1};MAP_STAGE={2};LAYER={3};SCREENSHOT={4};CAPTURED={5};DETAIL={6}" -f `
            $status.RunState, $status.CurrentChapter, $status.CurrentMapStage,
            $status.CurrentMapLayer, $screenshotPath, $captured, $status.StageDetail)
        exit 2
    }

    Start-Sleep -Seconds ([Math]::Max($StatusIntervalSeconds, 1))
}

$timeoutStatusPath = Join-Path $artifactRoot "qa-timeout-status.json"
$status | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $timeoutStatusPath -Encoding UTF8
$timeoutScreenshot = Join-Path $artifactRoot "qa-timeout-screen.png"
$captured = Save-ProcessWindowScreenshot $process $timeoutScreenshot
Write-Output ("QA_TIMEOUT=true;CHAPTER={0};MAP_STAGE={1};LAYER={2};SCREENSHOT={3};CAPTURED={4}" -f `
    $status.CurrentChapter, $status.CurrentMapStage, $status.CurrentMapLayer,
    $timeoutScreenshot, $captured)
exit 3
