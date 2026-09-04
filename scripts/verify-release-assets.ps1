[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$ReleaseDirectory,

    [string]$PackageDirectory,

    [string[]]$DeltaBaseArchive = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

. (Join-Path $PSScriptRoot 'common.ps1')

$repositoryRoot = Get-RepositoryRoot
$runtimeIdentifier = 'win-x64'
$packageVersion = $Version.Trim()
if ($packageVersion.StartsWith('v', [StringComparison]::OrdinalIgnoreCase)) {
    $packageVersion = $packageVersion.Substring(1)
}
if (-not (Test-CanonicalSemanticVersion -Value $packageVersion)) {
    throw "Version '$Version' is not a valid semantic version."
}

if ([string]::IsNullOrWhiteSpace($ReleaseDirectory)) {
    $ReleaseDirectory = Join-Path $repositoryRoot 'artifacts\release'
}
$ReleaseDirectory = [System.IO.Path]::GetFullPath($ReleaseDirectory)
if (-not (Test-Path -LiteralPath $ReleaseDirectory -PathType Container)) {
    throw "Release directory not found: $ReleaseDirectory"
}
if ([string]::IsNullOrWhiteSpace($PackageDirectory)) {
    $PackageDirectory = Join-Path $repositoryRoot 'artifacts\package\Loopstructor-2-QA-Tool'
}
$PackageDirectory = [System.IO.Path]::GetFullPath($PackageDirectory)
if (-not (Test-Path -LiteralPath $PackageDirectory -PathType Container)) {
    throw "Package directory not found: $PackageDirectory"
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-Sha256 {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.Stream]$Stream
    )

    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = $algorithm.ComputeHash($Stream)
        return (($bytes | ForEach-Object { $_.ToString('x2') }) -join '')
    }
    finally {
        $algorithm.Dispose()
    }
}

function Get-ZipFileIndex {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$RequiredRootDirectory
    )

    $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        if ($archive.Entries.Count -eq 0 -or $archive.Entries.Count -gt 10000) {
            throw "ZIP entry count is outside the allowed range: $Path"
        }
        $files = @()
        [long]$expandedTotal = 0
        foreach ($entry in $archive.Entries) {
            $entryName = $entry.FullName
            $isDirectory = $entryName.EndsWith('/', [StringComparison]::Ordinal)
            $pathForSegments = $entryName.TrimEnd('/')
            $segments = @($pathForSegments -split '/')
            if ($entryName.Contains('\') -or
                $entryName.StartsWith('/', [StringComparison]::Ordinal) -or
                $entryName.Contains(':') -or
                $segments.Count -eq 0 -or
                @($segments | Where-Object { [string]::IsNullOrWhiteSpace($_) -or $_ -eq '.' -or $_ -eq '..' }).Count -ne 0) {
                throw "ZIP contains an unsafe entry name: $entryName"
            }
            if (-not [StringComparer]::Ordinal.Equals($segments[0], $RequiredRootDirectory)) {
                throw "ZIP entry is outside the exact '$RequiredRootDirectory' root directory: $entryName"
            }
            $unixType = ($entry.ExternalAttributes -shr 16) -band 0xF000
            $windowsAttributes = [System.IO.FileAttributes]($entry.ExternalAttributes -band 0xFFFF)
            if ($unixType -eq 0xA000 -or $windowsAttributes.HasFlag([System.IO.FileAttributes]::ReparsePoint)) {
                throw "ZIP contains a link or reparse point: $entryName"
            }
            if ($isDirectory) {
                if ($segments.Count -ne 1) {
                    throw "ZIP contains an unexpected explicit directory entry: $entryName"
                }
                continue
            }
            if ($entry.Length -lt 0 -or $entry.Length -gt 536870912) {
                throw "ZIP entry is too large: $entryName"
            }
            if ($expandedTotal -gt 2147483648 - [long]$entry.Length) {
                throw "ZIP expanded size exceeds the allowed limit: $Path"
            }
            $expandedTotal += [long]$entry.Length
            if ($entry.Length -gt 1048576 -and
                $entry.CompressedLength -gt 0 -and
                [long]($entry.Length / $entry.CompressedLength) -gt 500) {
                throw "ZIP entry compression ratio is unsafe: $entryName"
            }
            if ($entryName.EndsWith('.zip', [StringComparison]::OrdinalIgnoreCase)) {
                throw "ZIP contains a nested archive: $entryName"
            }

            $stream = $entry.Open()
            try {
                $hash = Get-Sha256 -Stream $stream
            }
            finally {
                $stream.Dispose()
            }

            $files += [pscustomobject]@{
                Path = $entryName
                Length = [long]$entry.Length
                Sha256 = $hash
            }
        }

        $duplicates = @($files | Group-Object { $_.Path.ToLowerInvariant() } | Where-Object { $_.Count -gt 1 })
        if ($duplicates.Count -ne 0) {
            throw "ZIP contains duplicate or case-colliding file paths: $($duplicates.Name -join ', ')"
        }

        return @($files | Sort-Object Path)
    }
    finally {
        $archive.Dispose()
    }
}

function Get-DirectoryFileIndex {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $root = [System.IO.Path]::GetFullPath($Path).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    foreach ($directory in Get-ChildItem -LiteralPath $root -Recurse -Directory -Force) {
        if ($directory.Attributes.HasFlag([System.IO.FileAttributes]::ReparsePoint)) {
            throw "Package directory contains a reparse point: $($directory.FullName)"
        }
    }
    return @(
        Get-ChildItem -LiteralPath $root -Recurse -File -Force |
            ForEach-Object {
                [pscustomobject]@{
                    Path = $_.FullName.Substring($root.Length + 1).Replace('\', '/')
                    Length = [long]$_.Length
                    Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                }
            } |
            Sort-Object Path
    )
}

function Get-ChecksumCatalog {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Text
    )

    $catalog = @{}
    foreach ($rawLine in $Text -split "`r?`n") {
        if ([string]::IsNullOrEmpty($rawLine)) { continue }
        if ($rawLine -notmatch '^([0-9a-fA-F]{64})  (.+)$') {
            throw "checksums.sha256 contains an invalid line: $rawLine"
        }
        $relative = $Matches[2]
        $segments = @($relative -split '/')
        if ($relative.Contains('\') -or
            $relative.StartsWith('/', [StringComparison]::Ordinal) -or
            $relative.Contains(':') -or
            $segments.Count -eq 0 -or
            @($segments | Where-Object { [string]::IsNullOrWhiteSpace($_) -or $_ -eq '.' -or $_ -eq '..' }).Count -ne 0) {
            throw "checksums.sha256 contains an unsafe path: $relative"
        }
        $key = $relative.ToLowerInvariant()
        if ($catalog.ContainsKey($key)) {
            throw "checksums.sha256 contains a duplicate or case-colliding path: $relative"
        }
        $catalog[$key] = [pscustomobject]@{
            Path = $relative
            Sha256 = $Matches[1].ToLowerInvariant()
        }
    }
    if ($catalog.Count -eq 0 -or $catalog.Count -gt 10000) {
        throw 'checksums.sha256 entry count is outside the allowed range.'
    }
    return $catalog
}

function Assert-FileIndexesEqual {
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$Expected,

        [Parameter(Mandatory = $true)]
        [object[]]$Actual
    )

    if ($Expected.Count -ne $Actual.Count) {
        throw "ZIP contains $($Actual.Count) files, but the package directory contains $($Expected.Count)."
    }
    for ($index = 0; $index -lt $Expected.Count; $index++) {
        $expectedFile = $Expected[$index]
        $actualFile = $Actual[$index]
        if (-not [StringComparer]::Ordinal.Equals($expectedFile.Path, $actualFile.Path) -or
            [long]$expectedFile.Length -ne [long]$actualFile.Length -or
            -not [StringComparer]::OrdinalIgnoreCase.Equals($expectedFile.Sha256, $actualFile.Sha256)) {
            throw "ZIP differs from the package directory near '$($actualFile.Path)'."
        }
    }
}

function Read-ZipEntryText {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$EntryName
    )

    $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $matches = @($archive.Entries | Where-Object { [StringComparer]::Ordinal.Equals($_.FullName, $EntryName) })
        if ($matches.Count -ne 1) {
            throw "ZIP must contain exactly one '$EntryName' entry."
        }
        $stream = $matches[0].Open()
        try {
            $reader = [System.IO.StreamReader]::new($stream, [System.Text.Encoding]::UTF8, $true, 4096, $true)
            try {
                return $reader.ReadToEnd()
            }
            finally {
                $reader.Dispose()
            }
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Assert-Sidecar {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ArchivePath,

        [Parameter(Mandatory = $true)]
        [string]$ExpectedHash
    )

    $sidecarPath = "$ArchivePath.sha256"
    $archiveName = [System.IO.Path]::GetFileName($ArchivePath)
    $actual = [System.IO.File]::ReadAllText($sidecarPath).Trim()
    $expected = "$ExpectedHash  $archiveName"
    if (-not [StringComparer]::Ordinal.Equals($actual, $expected)) {
        throw "SHA-256 sidecar does not match $archiveName."
    }
}

$topLevelDirectory = 'Loopstructor-2-QA-Tool'
$deltaTopLevelDirectory = 'Loopstructor-2-QA-Tool.delta'
$archiveName = "Loopstructor-2-QA-Tool-$packageVersion-$runtimeIdentifier.zip"
$manifestName = 'autoplayer-update-manifest.json'
$manifestPath = Join-Path $ReleaseDirectory $manifestName
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$deltaAssets = @()
if (($manifest.PSObject.Properties.Name -contains 'deltaAssets') -and $null -ne $manifest.deltaAssets) {
    $deltaAssets = @($manifest.deltaAssets)
}
$expectedReleaseFiles = @(
    $archiveName
    "$archiveName.sha256"
    $manifestName
)
foreach ($delta in $deltaAssets) {
    $expectedReleaseFiles += [string]$delta.assetName
    $expectedReleaseFiles += ([string]$delta.assetName + '.sha256')
}
$expectedReleaseFiles = @($expectedReleaseFiles | Sort-Object)

$unexpectedDirectories = @(Get-ChildItem -LiteralPath $ReleaseDirectory -Directory -Force)
if ($unexpectedDirectories.Count -ne 0) {
    throw "Release directory contains unexpected subdirectories: $($unexpectedDirectories.Name -join ', ')"
}

$actualReleaseFiles = @(Get-ChildItem -LiteralPath $ReleaseDirectory -File -Force | ForEach-Object { $_.Name } | Sort-Object)
$releaseDifferences = @(Compare-Object -ReferenceObject $expectedReleaseFiles -DifferenceObject $actualReleaseFiles -CaseSensitive)
if ($releaseDifferences.Count -ne 0) {
    $summary = ($releaseDifferences | ForEach-Object { "$($_.SideIndicator) $($_.InputObject)" }) -join ', '
    throw "Release asset set is incorrect: $summary"
}

$archivePath = Join-Path $ReleaseDirectory $archiveName
$archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
Assert-Sidecar -ArchivePath $archivePath -ExpectedHash $archiveHash

$archiveFile = Get-Item -LiteralPath $archivePath
if ([int]$manifest.schemaVersion -ne 3 -or
    -not [StringComparer]::Ordinal.Equals([string]$manifest.version, $packageVersion) -or
    -not [StringComparer]::Ordinal.Equals([string]$manifest.runtimeIdentifier, $runtimeIdentifier) -or
    -not [StringComparer]::Ordinal.Equals([string]$manifest.assetName, $archiveName) -or
    -not [StringComparer]::OrdinalIgnoreCase.Equals([string]$manifest.sha256, $archiveHash) -or
    [long]$manifest.size -ne [long]$archiveFile.Length) {
    throw 'Update manifest does not exactly describe the wrapped release archive.'
}

$archiveFiles = @(Get-ZipFileIndex -Path $archivePath -RequiredRootDirectory $topLevelDirectory)
if ($archiveFiles.Count -eq 0) {
    throw 'Release archive must not be empty.'
}

$prefix = "$topLevelDirectory/"
$unwrappedArchiveFiles = @()
foreach ($file in $archiveFiles) {
    if (-not $file.Path.StartsWith($prefix, [StringComparison]::Ordinal)) {
        throw "Release archive contains a file outside its single top-level directory: $($file.Path)"
    }
    $unwrappedArchiveFiles += [pscustomobject]@{
        Path = $file.Path.Substring($prefix.Length)
        Length = $file.Length
        Sha256 = $file.Sha256
    }
}
$unwrappedArchiveFiles = @($unwrappedArchiveFiles | Sort-Object Path)
$packageFiles = @(Get-DirectoryFileIndex -Path $PackageDirectory)
Assert-FileIndexesEqual -Expected $packageFiles -Actual $unwrappedArchiveFiles

$packageFilesByPath = @{}
foreach ($file in $packageFiles) {
    $packageFilesByPath[$file.Path.ToLowerInvariant()] = $file
}
$targetChecksumText = [System.IO.File]::ReadAllText((Join-Path $PackageDirectory 'checksums.sha256'))
$targetChecksumCatalog = Get-ChecksumCatalog -Text $targetChecksumText
if ($packageFiles.Count -ne $targetChecksumCatalog.Count + 1) {
    throw 'Target checksums.sha256 does not describe every target package file.'
}
foreach ($entry in $targetChecksumCatalog.Values) {
    $key = $entry.Path.ToLowerInvariant()
    if (-not $packageFilesByPath.ContainsKey($key) -or
        -not [StringComparer]::Ordinal.Equals($packageFilesByPath[$key].Path, $entry.Path) -or
        -not [StringComparer]::Ordinal.Equals($packageFilesByPath[$key].Sha256, $entry.Sha256)) {
        throw "Target checksum catalog differs from the package directory: $($entry.Path)"
    }
}

if ($deltaAssets.Count -gt 16) {
    throw 'Update manifest contains too many incremental assets.'
}
$baseArchivesByVersion = @{}
$baseArchivePathsByVersion = @{}
foreach ($baseArchiveInput in $DeltaBaseArchive) {
    $baseArchivePath = [System.IO.Path]::GetFullPath($baseArchiveInput)
    if (-not (Test-Path -LiteralPath $baseArchivePath -PathType Leaf)) {
        throw "Delta base archive not found: $baseArchivePath"
    }
    $baseMarker = Read-ZipEntryText `
        -Path $baseArchivePath `
        -EntryName "$topLevelDirectory/autoplayer-release.json" | ConvertFrom-Json
    $baseVersion = [string]$baseMarker.version
    if (-not (Test-CanonicalSemanticVersion -Value $baseVersion) -or
        $baseArchivesByVersion.ContainsKey($baseVersion)) {
        throw "Delta base archive has an invalid or duplicate version: $baseVersion"
    }
    $baseArchiveFiles = @(Get-ZipFileIndex -Path $baseArchivePath -RequiredRootDirectory $topLevelDirectory)
    $baseFilesByPath = @{}
    foreach ($file in $baseArchiveFiles) {
        if (-not $file.Path.StartsWith($prefix, [StringComparison]::Ordinal)) {
            throw "Base release file is outside the fixed root: $($file.Path)"
        }
        $relative = $file.Path.Substring($prefix.Length)
        $baseFilesByPath[$relative.ToLowerInvariant()] = [pscustomobject]@{
            Path = $relative
            Length = $file.Length
            Sha256 = $file.Sha256
        }
    }
    $baseArchivesByVersion[$baseVersion] = $baseFilesByPath
    $baseArchivePathsByVersion[$baseVersion] = $baseArchivePath
}

$seenDeltaVersions = @{}
$seenDeltaNames = @{}
foreach ($delta in $deltaAssets) {
    $fromVersion = [string]$delta.fromVersion
    $deltaName = [string]$delta.assetName
    $expectedDeltaName = "Loopstructor-2-QA-Tool-$fromVersion-to-$packageVersion-$runtimeIdentifier.delta.zip"
    if (-not (Test-CanonicalSemanticVersion -Value $fromVersion) -or
        (Compare-CanonicalSemanticVersion -Left $fromVersion -Right $packageVersion) -ge 0 -or
        -not [StringComparer]::Ordinal.Equals($deltaName, $expectedDeltaName) -or
        $seenDeltaVersions.ContainsKey($fromVersion) -or
        $seenDeltaNames.ContainsKey($deltaName)) {
        throw "Update manifest contains an invalid incremental descriptor: $deltaName"
    }
    $seenDeltaVersions[$fromVersion] = $true
    $seenDeltaNames[$deltaName] = $true
    if (-not $baseArchivesByVersion.ContainsKey($fromVersion)) {
        throw "No verified base archive was supplied for incremental asset from $fromVersion."
    }

    $deltaPath = Join-Path $ReleaseDirectory $deltaName
    $deltaFile = Get-Item -LiteralPath $deltaPath
    $deltaHash = (Get-FileHash -LiteralPath $deltaPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ([long]$delta.size -ne [long]$deltaFile.Length -or
        [long]$deltaFile.Length -ge [long]$archiveFile.Length -or
        -not [StringComparer]::OrdinalIgnoreCase.Equals([string]$delta.sha256, $deltaHash)) {
        throw "Incremental asset metadata does not match $deltaName."
    }
    Assert-Sidecar -ArchivePath $deltaPath -ExpectedHash $deltaHash

    $deltaArchiveFiles = @(Get-ZipFileIndex -Path $deltaPath -RequiredRootDirectory $deltaTopLevelDirectory)
    $deltaPrefix = "$deltaTopLevelDirectory/"
    $deltaFilesByPath = @{}
    foreach ($file in $deltaArchiveFiles) {
        if (-not $file.Path.StartsWith($deltaPrefix, [StringComparison]::Ordinal)) {
            throw "Incremental ZIP file is outside the fixed root: $($file.Path)"
        }
        $relative = $file.Path.Substring($deltaPrefix.Length)
        $key = $relative.ToLowerInvariant()
        $deltaFilesByPath[$key] = [pscustomobject]@{
            Path = $relative
            Length = $file.Length
            Sha256 = $file.Sha256
        }
    }
    if (-not $deltaFilesByPath.ContainsKey('checksums.sha256')) {
        throw "Incremental ZIP is missing checksums.sha256: $deltaName"
    }
    $deltaChecksumText = Read-ZipEntryText `
        -Path $deltaPath `
        -EntryName "$deltaTopLevelDirectory/checksums.sha256"
    if (-not [StringComparer]::Ordinal.Equals($deltaChecksumText, $targetChecksumText)) {
        throw "Incremental ZIP does not carry the exact target checksum catalog: $deltaName"
    }

    $baseFilesByPath = $baseArchivesByVersion[$fromVersion]
    $expectedChanged = @{}
    foreach ($entry in $targetChecksumCatalog.Values) {
        $key = $entry.Path.ToLowerInvariant()
        if (-not $baseFilesByPath.ContainsKey($key) -or
            -not [StringComparer]::Ordinal.Equals($baseFilesByPath[$key].Sha256, $entry.Sha256)) {
            $expectedChanged[$key] = $entry
        }
    }
    $payloadFiles = @($deltaFilesByPath.Values | Where-Object {
        $_.Path.StartsWith('files/', [StringComparison]::Ordinal)
    })
    if ($payloadFiles.Count -ne $expectedChanged.Count -or
        $deltaFilesByPath.Count -ne $payloadFiles.Count + 1) {
        throw "Incremental ZIP contains missing or extra files: $deltaName"
    }
    foreach ($payload in $payloadFiles) {
        $targetRelative = $payload.Path.Substring('files/'.Length)
        $key = $targetRelative.ToLowerInvariant()
        if (-not $expectedChanged.ContainsKey($key) -or
            -not [StringComparer]::Ordinal.Equals($expectedChanged[$key].Path, $targetRelative) -or
            -not $packageFilesByPath.ContainsKey($key) -or
            -not [StringComparer]::Ordinal.Equals($payload.Sha256, $packageFilesByPath[$key].Sha256) -or
            [long]$payload.Length -ne [long]$packageFilesByPath[$key].Length) {
            throw "Incremental payload differs from the target package: $targetRelative"
        }
    }
    Write-Host "Verified incremental ZIP: $deltaName ($($expectedChanged.Count) changed files)"
}

$packagePaths = @($unwrappedArchiveFiles | ForEach-Object { $_.Path })
foreach ($requiredFile in @(
    'Loopstructor-2-QA-Tool.exe'
    'autoplayer-release.json'
    'checksums.sha256'
    'manager/Loopstructor-2-QA-Tool.exe'
    'manager/resources/app.asar'
    'manager/Loopstructor.AutoPlayer.Host.exe'
    'manager/Loopstructor.AutoPlayer.Host.dll'
    'manager/Loopstructor.AutoPlayer.Host.deps.json'
    'manager/Loopstructor.AutoPlayer.Host.runtimeconfig.json'
    'manager/Loopstructor.AutoPlayer.Updater.exe'
    'manager/Loopstructor.AutoPlayer.Updater.dll'
    'manager/Loopstructor.AutoPlayer.Updater.deps.json'
    'manager/Loopstructor.AutoPlayer.Updater.runtimeconfig.json'
    'manager/hostfxr.dll'
    'manager/hostpolicy.dll'
    'manager/coreclr.dll'
)) {
    if (-not ($packagePaths -ccontains $requiredFile)) {
        throw "Release archive is missing required file: $requiredFile"
    }
}
if (@($packagePaths | Where-Object {
    $_.StartsWith('updater/', [StringComparison]::Ordinal)
}).Count -ne 0) {
    throw 'Release archive must not contain the retired updater/ compatibility directory.'
}
foreach ($requiredDirectory in @('manager/', 'payload/')) {
    if (@($packagePaths | Where-Object { $_.StartsWith($requiredDirectory, [StringComparison]::Ordinal) }).Count -eq 0) {
        throw "Release archive is missing required directory content: $requiredDirectory"
    }
}

$markerEntryName = "$prefix" + 'autoplayer-release.json'
$marker = Read-ZipEntryText -Path $archivePath -EntryName $markerEntryName | ConvertFrom-Json
if ([int]$marker.schemaVersion -ne 2 -or
    -not [StringComparer]::Ordinal.Equals([string]$marker.version, $packageVersion) -or
    -not [StringComparer]::Ordinal.Equals([string]$marker.managerPath, 'Loopstructor-2-QA-Tool.exe') -or
    -not [StringComparer]::Ordinal.Equals([string]$marker.updaterPath, 'manager/Loopstructor.AutoPlayer.Updater.exe')) {
    throw 'Release marker schema, version, root Manager entry point, or Updater entry point is incorrect.'
}

Write-Host "Verified release ZIP: $archiveName"
Write-Host "Verified single top-level directory: $topLevelDirectory"
Write-Host "Verified $($unwrappedArchiveFiles.Count) package files byte-for-byte."

$verificationProject = Join-Path $repositoryRoot 'src\Loopstructor.AutoPlayer.Updater\Verification\Verification.csproj'
Invoke-DotNet -Arguments @(
    'run'
    '--project', $verificationProject
    '--configuration', 'Release'
    '--no-restore'
    '--no-build'
    '--'
    '--verify-release-package', $archivePath
    '--expected-version', $packageVersion
)
foreach ($delta in $deltaAssets) {
    $fromVersion = [string]$delta.fromVersion
    Invoke-DotNet -Arguments @(
        'run'
        '--project', $verificationProject
        '--configuration', 'Release'
        '--no-restore'
        '--no-build'
        '--'
        '--verify-delta-package', (Join-Path $ReleaseDirectory ([string]$delta.assetName))
        '--base-package', [string]$baseArchivePathsByVersion[$fromVersion]
        '--expected-base-version', $fromVersion
        '--expected-version', $packageVersion
    )
}
