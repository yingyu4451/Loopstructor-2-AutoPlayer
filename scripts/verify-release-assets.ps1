[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$ReleaseDirectory,

    [string]$PackageDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

. (Join-Path $PSScriptRoot 'common.ps1')

$repositoryRoot = Get-RepositoryRoot
$runtimeIdentifier = 'win-x64'
$semanticVersionPattern = '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$'
$packageVersion = $Version.Trim()
if ($packageVersion.StartsWith('v', [StringComparison]::OrdinalIgnoreCase)) {
    $packageVersion = $packageVersion.Substring(1)
}
if ($packageVersion -notmatch $semanticVersionPattern) {
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
    $PackageDirectory = Join-Path $repositoryRoot 'artifacts\package\Loopstructor.AutoPlayer'
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
        $files = @()
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
            if ($isDirectory) {
                if ($segments.Count -ne 1) {
                    throw "ZIP contains an unexpected explicit directory entry: $entryName"
                }
                continue
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

$topLevelDirectory = 'Loopstructor 2.AutoPlayer'
$archiveName = "Loopstructor.AutoPlayer-$packageVersion-$runtimeIdentifier.zip"
$manifestName = 'autoplayer-update-manifest.json'
$expectedReleaseFiles = @(
    $archiveName
    "$archiveName.sha256"
    $manifestName
) | Sort-Object

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
$manifestPath = Join-Path $ReleaseDirectory $manifestName
$archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
Assert-Sidecar -ArchivePath $archivePath -ExpectedHash $archiveHash

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$archiveFile = Get-Item -LiteralPath $archivePath
if ([int]$manifest.schemaVersion -ne 2 -or
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

$packagePaths = @($unwrappedArchiveFiles | ForEach-Object { $_.Path })
foreach ($requiredFile in @(
    'Loopstructor.AutoPlayer.Manager.exe'
    'autoplayer-release.json'
    'checksums.sha256'
    'manager/Loopstructor.AutoPlayer.Manager.exe'
    'updater/Loopstructor.AutoPlayer.Updater.exe'
    'updater/Loopstructor.AutoPlayer.Updater.runtimeconfig.json'
    'updater/hostfxr.dll'
    'updater/hostpolicy.dll'
    'updater/coreclr.dll'
)) {
    if (-not ($packagePaths -ccontains $requiredFile)) {
        throw "Release archive is missing required file: $requiredFile"
    }
}

$managerPackageFiles = @($packagePaths | Where-Object {
    $_.StartsWith('manager/', [StringComparison]::Ordinal)
})
if ($managerPackageFiles.Count -ne 1 -or
    -not [StringComparer]::Ordinal.Equals(
        $managerPackageFiles[0],
        'manager/Loopstructor.AutoPlayer.Manager.exe')) {
    throw "Internal Manager must be exactly one self-contained EXE: $($managerPackageFiles -join ', ')"
}
foreach ($requiredDirectory in @('manager/', 'payload/', 'updater/')) {
    if (@($packagePaths | Where-Object { $_.StartsWith($requiredDirectory, [StringComparison]::Ordinal) }).Count -eq 0) {
        throw "Release archive is missing required directory content: $requiredDirectory"
    }
}

$markerEntryName = "$prefix" + 'autoplayer-release.json'
$marker = Read-ZipEntryText -Path $archivePath -EntryName $markerEntryName | ConvertFrom-Json
if (-not [StringComparer]::Ordinal.Equals([string]$marker.version, $packageVersion) -or
    -not [StringComparer]::Ordinal.Equals([string]$marker.managerPath, 'Loopstructor.AutoPlayer.Manager.exe')) {
    throw 'Release marker version or root Manager entry point is incorrect.'
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
