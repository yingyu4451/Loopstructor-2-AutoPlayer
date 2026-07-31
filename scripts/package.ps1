[CmdletBinding()]
param(
    [string]$Version,

    [switch]$SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

. (Join-Path $PSScriptRoot 'common.ps1')

$repositoryRoot = Get-RepositoryRoot
$runtimeIdentifier = 'win-x64'
$launcherProject = Join-Path $repositoryRoot 'src\Loopstructor.AutoPlayer.Launcher\Loopstructor.AutoPlayer.Launcher.csproj'
$managerProject = Join-Path $repositoryRoot 'src\Loopstructor.AutoPlayer.Manager\Loopstructor.AutoPlayer.Manager.csproj'
$updaterProject = Join-Path $repositoryRoot 'src\Loopstructor.AutoPlayer.Updater\Loopstructor.AutoPlayer.Updater.csproj'
$pluginProject = Join-Path $repositoryRoot 'src\Loopstructor.AutoPlayer.Plugin\Loopstructor.AutoPlayer.Plugin.csproj'
$pluginInfoPath = Join-Path $repositoryRoot 'src\Loopstructor.AutoPlayer.Plugin\PluginInfo.cs'
$pluginOutput = Join-Path $repositoryRoot 'src\Loopstructor.AutoPlayer.Plugin\bin\Release\netstandard2.1'
$artifactsRoot = Join-Path $repositoryRoot 'artifacts'
$packageWorkRoot = Join-Path $artifactsRoot 'package'
$packageRoot = Join-Path $packageWorkRoot 'Loopstructor.AutoPlayer'
$releaseRoot = Join-Path $artifactsRoot 'release'

function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Content
    )

    [System.IO.File]::WriteAllText($Path, $Content, (New-Object System.Text.UTF8Encoding($false)))
}

function Get-RelativePackagePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    return $Path.Substring($packageRoot.Length + 1).Replace('\', '/')
}

function Get-BepInExRuntimeInfo {
    $propsPath = Join-Path $repositoryRoot 'Directory.Build.props'
    [xml]$props = Get-Content -LiteralPath $propsPath -Raw
    $propertyGroup = @($props.Project.PropertyGroup) | Where-Object { $_.BepInExRuntimeVersion } | Select-Object -First 1
    if (-not $propertyGroup) {
        throw "BepInEx runtime properties are missing from $propsPath."
    }

    $runtimeVersion = [string]$propertyGroup.BepInExRuntimeVersion
    $archiveName = ([string]$propertyGroup.BepInExRuntimeArchiveName).Replace('$(BepInExRuntimeVersion)', $runtimeVersion)
    $downloadUrl = ([string]$propertyGroup.BepInExRuntimeDownloadUrl).
        Replace('$(BepInExRuntimeVersion)', $runtimeVersion).
        Replace('$(BepInExRuntimeArchiveName)', $archiveName)
    $sha256 = ([string]$propertyGroup.BepInExRuntimeSha256).ToLowerInvariant()

    if ($sha256 -notmatch '^[0-9a-f]{64}$') {
        throw "Invalid BepInEx SHA-256 in $propsPath."
    }

    return [pscustomobject]@{
        Version = $runtimeVersion
        ArchiveName = $archiveName
        DownloadUrl = $downloadUrl
        Sha256 = $sha256
    }
}

function Get-ReleaseVersion {
    if ([string]::IsNullOrWhiteSpace($Version)) {
        [xml]$props = Get-Content -LiteralPath (Join-Path $repositoryRoot 'Directory.Build.props') -Raw
        $Version = [string](@($props.Project.PropertyGroup) | Where-Object { $_.VersionPrefix } | Select-Object -First 1).VersionPrefix
    }

    $normalized = $Version.Trim()
    if ($normalized.StartsWith('v', [StringComparison]::OrdinalIgnoreCase)) {
        $normalized = $normalized.Substring(1)
    }

    $semanticVersionPattern = '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$'
    if ($normalized -notmatch $semanticVersionPattern) {
        throw "Version '$Version' is not a valid semantic version."
    }

    return $normalized
}

function Get-VerifiedBepInExArchive {
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$RuntimeInfo
    )

    $downloadRoot = Join-Path $repositoryRoot '.tools\bepinex'
    $archivePath = Join-Path $downloadRoot $RuntimeInfo.ArchiveName
    New-Item -ItemType Directory -Path $downloadRoot -Force | Out-Null

    if (Test-Path -LiteralPath $archivePath -PathType Leaf) {
        $existingHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($existingHash -eq $RuntimeInfo.Sha256) {
            return $archivePath
        }
    }

    $partialPath = "$archivePath.partial"
    if (Test-Path -LiteralPath $partialPath) {
        Remove-Item -LiteralPath $partialPath -Force
    }

    Write-Host "Downloading BepInEx $($RuntimeInfo.Version)..."
    Invoke-WebRequest -UseBasicParsing -Uri $RuntimeInfo.DownloadUrl -OutFile $partialPath
    $downloadedHash = (Get-FileHash -LiteralPath $partialPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($downloadedHash -ne $RuntimeInfo.Sha256) {
        Remove-Item -LiteralPath $partialPath -Force
        throw "BepInEx archive verification failed. Expected $($RuntimeInfo.Sha256), got $downloadedHash."
    }

    Move-Item -LiteralPath $partialPath -Destination $archivePath -Force
    return $archivePath
}

function Publish-WindowsProject {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Project,

        [Parameter(Mandatory = $true)]
        [string]$Output,

        [Parameter(Mandatory = $true)]
        [string]$PackageVersion
    )

    Invoke-DotNet -Arguments @(
        'restore',
        $Project,
        '--runtime', $runtimeIdentifier,
        '--configfile', $script:NuGetConfigPath,
        '--verbosity', 'minimal'
    )

    Invoke-DotNet -Arguments @(
        'publish',
        $Project,
        '--configuration', 'Release',
        '--runtime', $runtimeIdentifier,
        '--self-contained', 'true',
        '--no-restore',
        '--output', $Output,
        '--nologo',
        "-p:Version=$PackageVersion",
        '-p:PublishSingleFile=false',
        '-p:PublishReadyToRun=false'
    )
}

function Publish-RootLauncher {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Project,

        [Parameter(Mandatory = $true)]
        [string]$Output,

        [Parameter(Mandatory = $true)]
        [string]$PackageVersion
    )

    Invoke-DotNet -Arguments @(
        'restore',
        $Project,
        '--runtime', $runtimeIdentifier,
        '--configfile', $script:NuGetConfigPath,
        '--verbosity', 'minimal',
        '-p:PublishRootLauncher=true'
    )

    Invoke-DotNet -Arguments @(
        'publish',
        $Project,
        '--configuration', 'Release',
        '--runtime', $runtimeIdentifier,
        '--self-contained', 'true',
        '--no-restore',
        '--output', $Output,
        '--nologo',
        "-p:Version=$PackageVersion",
        '-p:PublishRootLauncher=true'
    )
}

function New-DeterministicZip {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourceDirectory,

        [Parameter(Mandatory = $true)]
        [string]$Destination,

        [string]$EntryPrefix = ''
    )

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    if (Test-Path -LiteralPath $Destination) {
        Remove-Item -LiteralPath $Destination -Force
    }

    $archive = [System.IO.Compression.ZipFile]::Open($Destination, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $normalizedPrefix = $EntryPrefix.Trim().Trim('/')
        $files = Get-ChildItem -LiteralPath $SourceDirectory -Recurse -File -Force | Sort-Object FullName
        foreach ($file in $files) {
            $relativeEntry = $file.FullName.Substring($SourceDirectory.Length + 1).Replace('\', '/')
            $entryName = if ([string]::IsNullOrWhiteSpace($normalizedPrefix)) {
                $relativeEntry
            }
            else {
                "$normalizedPrefix/$relativeEntry"
            }
            $entry = $archive.CreateEntry($entryName, [System.IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [DateTimeOffset]::Parse('1980-01-01T00:00:00Z')

            $input = [System.IO.File]::OpenRead($file.FullName)
            $output = $entry.Open()
            try {
                $input.CopyTo($output)
            }
            finally {
                $output.Dispose()
                $input.Dispose()
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Assert-ZipMatchesDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ZipPath,

        [Parameter(Mandatory = $true)]
        [string]$SourceDirectory,

        [string]$EntryPrefix = ''
    )

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $normalizedPrefix = $EntryPrefix.Trim().Trim('/')
    $expectedEntries = @(
        Get-ChildItem -LiteralPath $SourceDirectory -Recurse -File -Force |
            ForEach-Object {
                $relativeEntry = $_.FullName.Substring($SourceDirectory.Length + 1).Replace('\', '/')
                if ([string]::IsNullOrWhiteSpace($normalizedPrefix)) {
                    $relativeEntry
                }
                else {
                    "$normalizedPrefix/$relativeEntry"
                }
            }
    ) | Sort-Object

    $archive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        $actualEntries = @(
            $archive.Entries |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_.Name) } |
                ForEach-Object { $_.FullName.Replace('\', '/') }
        ) | Sort-Object

        $differences = @(Compare-Object -ReferenceObject $expectedEntries -DifferenceObject $actualEntries -CaseSensitive)
        if ($differences.Count -ne 0) {
            $summary = ($differences | Select-Object -First 10 | ForEach-Object { "$($_.SideIndicator) $($_.InputObject)" }) -join ', '
            throw "ZIP layout does not match the package directory: $ZipPath ($summary)"
        }

        $nestedArchives = @($actualEntries | Where-Object { $_.EndsWith('.zip', [StringComparison]::OrdinalIgnoreCase) })
        if ($nestedArchives.Count -ne 0) {
            throw "ZIP contains a nested archive: $($nestedArchives -join ', ')"
        }

        if (-not [string]::IsNullOrWhiteSpace($normalizedPrefix)) {
            $topLevelNames = @(
                $actualEntries |
                    ForEach-Object { ($_ -split '/', 2)[0] } |
                    Sort-Object -Unique
            )
            if ($topLevelNames.Count -ne 1 -or
                -not [StringComparer]::Ordinal.Equals($topLevelNames[0], $normalizedPrefix)) {
                throw "Download ZIP must contain exactly one top-level directory named $normalizedPrefix."
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

foreach ($requiredProject in @($launcherProject, $managerProject, $updaterProject, $pluginProject)) {
    if (-not (Test-Path -LiteralPath $requiredProject -PathType Leaf)) {
        throw "Required release project not found: $requiredProject"
    }
}

$packageVersion = Get-ReleaseVersion
$pluginInfoSource = Get-Content -LiteralPath $pluginInfoPath -Raw
$pluginVersionMatch = [regex]::Match(
    $pluginInfoSource,
    'public\s+const\s+string\s+Version\s*=\s*"(?<version>[^"]+)"\s*;')
if (-not $pluginVersionMatch.Success -or
    -not [StringComparer]::Ordinal.Equals($pluginVersionMatch.Groups['version'].Value, $packageVersion)) {
    throw "PluginInfo.Version must exactly match package version $packageVersion."
}
$bepInEx = Get-BepInExRuntimeInfo

if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot 'build.ps1') -Configuration Release -Version $packageVersion
}

if (-not (Test-Path -LiteralPath (Join-Path $pluginOutput 'Loopstructor.AutoPlayer.Plugin.dll') -PathType Leaf)) {
    throw "Plugin output is missing. Run scripts\build.ps1 before packaging."
}

foreach ($directory in @($packageWorkRoot, $releaseRoot)) {
    if (Test-Path -LiteralPath $directory) {
        Remove-Item -LiteralPath $directory -Recurse -Force
    }
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}

$managerOutput = Join-Path $packageRoot 'manager'
$updaterOutput = Join-Path $packageRoot 'updater'
$launcherOutput = Join-Path $packageWorkRoot 'launcher'
$payloadOutput = Join-Path $packageRoot 'payload'
$bepInExPayloadOutput = Join-Path $payloadOutput 'bepinex'
$pluginPayloadOutput = Join-Path $payloadOutput 'plugin'

Publish-WindowsProject -Project $managerProject -Output $managerOutput -PackageVersion $packageVersion
Publish-WindowsProject -Project $updaterProject -Output $updaterOutput -PackageVersion $packageVersion
Publish-RootLauncher -Project $launcherProject -Output $launcherOutput -PackageVersion $packageVersion

$launcherFiles = @(Get-ChildItem -LiteralPath $launcherOutput -Recurse -File -Force)
$launcherExecutable = Join-Path $launcherOutput 'Loopstructor.AutoPlayer.Launcher.exe'
if ($launcherFiles.Count -ne 1 -or -not (Test-Path -LiteralPath $launcherExecutable -PathType Leaf)) {
    $launcherNames = ($launcherFiles | ForEach-Object { $_.FullName.Substring($launcherOutput.Length + 1) }) -join ', '
    throw "Root launcher must publish as exactly one executable. Found: $launcherNames"
}

$rootManagerExecutable = Join-Path $packageRoot 'Loopstructor.AutoPlayer.Manager.exe'
Copy-Item -LiteralPath $launcherExecutable -Destination $rootManagerExecutable

$bepInExArchive = Get-VerifiedBepInExArchive -RuntimeInfo $bepInEx
New-Item -ItemType Directory -Path $bepInExPayloadOutput -Force | Out-Null
Expand-Archive -LiteralPath $bepInExArchive -DestinationPath $bepInExPayloadOutput
New-Item -ItemType Directory -Path $pluginPayloadOutput -Force | Out-Null

$excludedRuntimePatterns = @(
    'BepInEx*',
    '0Harmony*',
    'HarmonyXInterop*',
    'Mono.Cecil*',
    'MonoMod.*',
    'UnityEngine*'
)

Get-ChildItem -LiteralPath $pluginOutput -File | Where-Object {
    $fileName = $_.Name
    -not ($excludedRuntimePatterns | Where-Object { $fileName -like $_ })
} | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $pluginPayloadOutput
}

$marker = [ordered]@{
    schemaVersion = 1
    version = $packageVersion
    runtimeIdentifier = $runtimeIdentifier
    managerPath = 'Loopstructor.AutoPlayer.Manager.exe'
    updaterPath = 'updater/Loopstructor.AutoPlayer.Updater.exe'
    payloadPath = 'payload'
    bepInExPayloadPath = 'payload/bepinex'
    pluginPayloadPath = 'payload/plugin'
    pluginPath = 'payload/plugin/Loopstructor.AutoPlayer.Plugin.dll'
    bepInExVersion = $bepInEx.Version
}
$markerJson = $marker | ConvertTo-Json -Depth 4
Write-Utf8NoBom -Path (Join-Path $packageRoot 'autoplayer-release.json') -Content $markerJson
Write-Utf8NoBom -Path (Join-Path $packageRoot 'version.json') -Content $markerJson

$checksumLines = Get-ChildItem -LiteralPath $packageRoot -Recurse -File -Force |
    Sort-Object FullName |
    ForEach-Object {
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $(Get-RelativePackagePath -Path $_.FullName)"
    }
Write-Utf8NoBom -Path (Join-Path $packageRoot 'checksums.sha256') -Content (($checksumLines -join "`n") + "`n")

$releaseDirectoryName = 'Loopstructor 2.AutoPlayer'
$zipName = "Loopstructor.AutoPlayer-$packageVersion-$runtimeIdentifier.zip"
$zipPath = Join-Path $releaseRoot $zipName
New-DeterministicZip -SourceDirectory $packageRoot -Destination $zipPath -EntryPrefix $releaseDirectoryName
Assert-ZipMatchesDirectory -ZipPath $zipPath -SourceDirectory $packageRoot -EntryPrefix $releaseDirectoryName

$zipFile = Get-Item -LiteralPath $zipPath
$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Utf8NoBom -Path "$zipPath.sha256" -Content "$zipHash  $zipName`n"

$updateManifest = [ordered]@{
    schemaVersion = 2
    version = $packageVersion
    runtimeIdentifier = $runtimeIdentifier
    assetName = $zipName
    sha256 = $zipHash
    size = [long]$zipFile.Length
}
$manifestPath = Join-Path $releaseRoot 'autoplayer-update-manifest.json'
Write-Utf8NoBom -Path $manifestPath -Content ($updateManifest | ConvertTo-Json -Depth 4)

Write-Host "Release package: $zipPath"
Write-Host "SHA-256: $zipHash"
Write-Host "Update manifest: $manifestPath"
