[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BaseArchive,

    [Parameter(Mandatory = $true)]
    [string]$BaseManifest,

    [Parameter(Mandatory = $true)]
    [string]$TargetPackageDirectory,

    [Parameter(Mandatory = $true)]
    [string]$TargetVersion,

    [Parameter(Mandatory = $true)]
    [string]$ReleaseDirectory,

    [string]$SevenZipPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

. (Join-Path $PSScriptRoot 'common.ps1')

$runtimeIdentifier = 'win-x64'
$releaseRootName = 'Loopstructor 2.AutoPlayer'
$deltaRootName = 'Loopstructor 2.AutoPlayer.delta'
$manifestName = 'autoplayer-update-manifest.json'
$BaseArchive = [System.IO.Path]::GetFullPath($BaseArchive)
$BaseManifest = [System.IO.Path]::GetFullPath($BaseManifest)
$TargetPackageDirectory = [System.IO.Path]::GetFullPath($TargetPackageDirectory).TrimEnd('\', '/')
$ReleaseDirectory = [System.IO.Path]::GetFullPath($ReleaseDirectory).TrimEnd('\', '/')
$targetPackageVersion = $TargetVersion.Trim().TrimStart('v', 'V')

foreach ($requiredFile in @($BaseArchive, $BaseManifest)) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Required delta input file not found: $requiredFile"
    }
}
foreach ($requiredDirectory in @($TargetPackageDirectory, $ReleaseDirectory)) {
    if (-not (Test-Path -LiteralPath $requiredDirectory -PathType Container)) {
        throw "Required delta input directory not found: $requiredDirectory"
    }
}
if (-not [StringComparer]::Ordinal.Equals(
        [System.IO.Path]::GetFileName($TargetPackageDirectory),
        $releaseRootName)) {
    throw "Target package directory must be named exactly '$releaseRootName'."
}
if (-not (Test-CanonicalSemanticVersion -Value $targetPackageVersion)) {
    throw "Target version '$TargetVersion' is not a canonical semantic version."
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Write-Utf8NoBom {
    param([string]$Path, [string]$Content)
    [System.IO.File]::WriteAllText($Path, $Content, (New-Object System.Text.UTF8Encoding($false)))
}

function Get-StreamSha256 {
    param([System.IO.Stream]$Stream)
    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try {
        return (($algorithm.ComputeHash($Stream) | ForEach-Object { $_.ToString('x2') }) -join '')
    }
    finally {
        $algorithm.Dispose()
    }
}

function Assert-PortablePath {
    param([string]$Path, [string]$Label)
    if ([string]::IsNullOrWhiteSpace($Path) -or
        $Path.Contains('\') -or
        $Path.StartsWith('/', [StringComparison]::Ordinal) -or
        $Path.Contains(':')) {
        throw "$Label contains an unsafe path: $Path"
    }
    $segments = @($Path -split '/')
    if ($segments.Count -eq 0 -or
        @($segments | Where-Object { [string]::IsNullOrWhiteSpace($_) -or $_ -eq '.' -or $_ -eq '..' }).Count -ne 0) {
        throw "$Label contains an unsafe path: $Path"
    }
}

function Get-ZipFileIndex {
    param([string]$Path, [string]$RequiredRootDirectory)
    $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        if ($archive.Entries.Count -eq 0 -or $archive.Entries.Count -gt 10000) {
            throw "ZIP entry count is outside the allowed range: $Path"
        }
        $files = @()
        [long]$expandedTotal = 0
        foreach ($entry in $archive.Entries) {
            $normalized = $entry.FullName.Replace('\', '/')
            $isDirectory = $normalized.EndsWith('/', [StringComparison]::Ordinal)
            $normalized = $normalized.TrimEnd('/')
            Assert-PortablePath -Path $normalized -Label 'ZIP'
            $segments = @($normalized -split '/')
            if (-not [StringComparer]::Ordinal.Equals($segments[0], $RequiredRootDirectory)) {
                throw "ZIP entry is outside '$RequiredRootDirectory': $($entry.FullName)"
            }
            $unixType = ($entry.ExternalAttributes -shr 16) -band 0xF000
            $windowsAttributes = [System.IO.FileAttributes]($entry.ExternalAttributes -band 0xFFFF)
            if ($unixType -eq 0xA000 -or $windowsAttributes.HasFlag([System.IO.FileAttributes]::ReparsePoint)) {
                throw "ZIP contains a link or reparse point: $($entry.FullName)"
            }
            if ($segments.Count -eq 1) {
                if (-not $isDirectory) { throw "ZIP root entry must be a directory: $($entry.FullName)" }
                continue
            }
            if ($isDirectory) { continue }
            if ($entry.Length -lt 0 -or $entry.Length -gt 536870912) {
                throw "ZIP entry is too large: $($entry.FullName)"
            }
            if ($expandedTotal -gt 2147483648 - [long]$entry.Length) {
                throw "ZIP expanded size exceeds the allowed limit: $Path"
            }
            $expandedTotal += [long]$entry.Length
            if ($entry.Length -gt 1048576 -and
                $entry.CompressedLength -gt 0 -and
                [long]($entry.Length / $entry.CompressedLength) -gt 500) {
                throw "ZIP entry compression ratio is unsafe: $($entry.FullName)"
            }
            $relative = ($segments[1..($segments.Count - 1)] -join '/')
            $stream = $entry.Open()
            try { $hash = Get-StreamSha256 -Stream $stream } finally { $stream.Dispose() }
            $files += [pscustomobject]@{
                Path = $relative
                Length = [long]$entry.Length
                Sha256 = $hash
                EntryName = $entry.FullName
            }
        }
        $duplicates = @($files | Group-Object { $_.Path.ToLowerInvariant() } | Where-Object { $_.Count -gt 1 })
        if ($duplicates.Count -ne 0) {
            throw "ZIP contains duplicate or case-colliding paths: $($duplicates.Name -join ', ')"
        }
        return @($files | Sort-Object Path)
    }
    finally {
        $archive.Dispose()
    }
}

function Read-ZipEntryText {
    param([string]$Path, [string]$EntryName)
    $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entries = @($archive.Entries | Where-Object { [StringComparer]::Ordinal.Equals($_.FullName, $EntryName) })
        if ($entries.Count -ne 1) { throw "ZIP must contain exactly one '$EntryName' entry." }
        $stream = $entries[0].Open()
        try {
            $reader = [System.IO.StreamReader]::new($stream, [System.Text.Encoding]::UTF8, $true, 4096, $true)
            try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
        }
        finally { $stream.Dispose() }
    }
    finally { $archive.Dispose() }
}

function Get-DirectoryFileIndex {
    param([string]$Path)
    $root = [System.IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $files = @()
    foreach ($directory in Get-ChildItem -LiteralPath $root -Recurse -Directory -Force) {
        if ($directory.Attributes.HasFlag([System.IO.FileAttributes]::ReparsePoint)) {
            throw "Target package contains a reparse point: $($directory.FullName)"
        }
    }
    foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File -Force) {
        if ($file.Attributes.HasFlag([System.IO.FileAttributes]::ReparsePoint)) {
            throw "Target package contains a reparse point: $($file.FullName)"
        }
        $files += [pscustomobject]@{
            Path = $file.FullName.Substring($root.Length + 1).Replace('\', '/')
            Length = [long]$file.Length
            Sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    $duplicates = @($files | Group-Object { $_.Path.ToLowerInvariant() } | Where-Object { $_.Count -gt 1 })
    if ($duplicates.Count -ne 0) { throw "Target package contains case-colliding paths." }
    return @($files | Sort-Object Path)
}

function Read-ChecksumCatalog {
    param([string]$Path)
    $catalog = @{}
    foreach ($rawLine in [System.IO.File]::ReadAllLines($Path)) {
        if ($rawLine -notmatch '^([0-9a-fA-F]{64})  (.+)$') {
            throw "Invalid checksums.sha256 line: $rawLine"
        }
        $relative = $Matches[2]
        Assert-PortablePath -Path $relative -Label 'checksums.sha256'
        $key = $relative.ToLowerInvariant()
        if ($catalog.ContainsKey($key)) { throw "Duplicate checksum path: $relative" }
        $catalog[$key] = [pscustomobject]@{ Path = $relative; Sha256 = $Matches[1].ToLowerInvariant() }
    }
    if ($catalog.Count -eq 0 -or $catalog.Count -gt 10000) {
        throw 'checksums.sha256 entry count is outside the allowed range.'
    }
    return $catalog
}

function Resolve-SevenZipExecutable {
    param([string]$RequestedPath)
    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $resolved = [System.IO.Path]::GetFullPath($RequestedPath)
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) { throw "7-Zip not found: $resolved" }
        return $resolved
    }
    $command = Get-Command '7z.exe' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) { return $command.Source }
    foreach ($programFilesRoot in @($env:ProgramW6432, $env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        if ([string]::IsNullOrWhiteSpace($programFilesRoot)) { continue }
        $candidate = Join-Path $programFilesRoot '7-Zip\7z.exe'
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return [System.IO.Path]::GetFullPath($candidate) }
    }
    throw '7-Zip is required to create the incremental package.'
}

function New-MaximumCompressionZip {
    param([string]$SourceDirectory, [string]$Destination, [string]$SevenZipExecutable, [string]$ListPath)
    $sourceRoot = [System.IO.Path]::GetFullPath($SourceDirectory).TrimEnd('\', '/')
    $sourceParent = [System.IO.Directory]::GetParent($sourceRoot).FullName
    $files = @(Get-ChildItem -LiteralPath $sourceRoot -Recurse -File -Force | Sort-Object FullName)
    if ($files.Count -eq 0) { throw 'Incremental package staging directory is empty.' }
    $entryPaths = @($files | ForEach-Object { $_.FullName.Substring($sourceParent.Length + 1).Replace('\', '/') })
    [System.IO.File]::WriteAllLines($ListPath, $entryPaths, (New-Object System.Text.UTF8Encoding($false)))
    if (Test-Path -LiteralPath $Destination) { Remove-Item -LiteralPath $Destination -Force }
    $arguments = @(
        'a', '-tzip', $Destination, "@$ListPath", '-scsUTF-8', '-mm=Deflate', '-mx=9', '-mfb=258',
        '-mpass=15', '-mtc=off', '-mtm=off', '-mta=off', '-bd', '-bb0', '-y'
    )
    Push-Location $sourceParent
    try {
        & $SevenZipExecutable @arguments
        if ($LASTEXITCODE -ne 0) { throw "7-Zip failed with exit code $LASTEXITCODE." }
    }
    finally { Pop-Location }
}

$baseUpdateManifest = Get-Content -LiteralPath $BaseManifest -Raw | ConvertFrom-Json
$baseVersion = [string]$baseUpdateManifest.version
if ([int]$baseUpdateManifest.schemaVersion -ne 2 -or
    -not (Test-CanonicalSemanticVersion -Value $baseVersion) -or
    -not [StringComparer]::Ordinal.Equals([string]$baseUpdateManifest.runtimeIdentifier, $runtimeIdentifier) -or
    -not [StringComparer]::Ordinal.Equals([string]$baseUpdateManifest.assetName, [System.IO.Path]::GetFileName($BaseArchive)) -or
    [long]$baseUpdateManifest.size -ne [long](Get-Item -LiteralPath $BaseArchive).Length) {
    throw 'Base update manifest does not describe the supplied base archive.'
}
$baseArchiveHash = (Get-FileHash -LiteralPath $BaseArchive -Algorithm SHA256).Hash.ToLowerInvariant()
if (-not [StringComparer]::OrdinalIgnoreCase.Equals([string]$baseUpdateManifest.sha256, $baseArchiveHash)) {
    throw 'Base archive SHA-256 does not match its published manifest.'
}
if ((Compare-CanonicalSemanticVersion -Left $baseVersion -Right $targetPackageVersion) -ge 0) {
    throw 'Base version must be earlier than the target version.'
}

$targetManifestPath = Join-Path $ReleaseDirectory $manifestName
$targetUpdateManifest = Get-Content -LiteralPath $targetManifestPath -Raw | ConvertFrom-Json
$targetFullArchive = Join-Path $ReleaseDirectory ([string]$targetUpdateManifest.assetName)
if ([int]$targetUpdateManifest.schemaVersion -ne 2 -or
    -not [StringComparer]::Ordinal.Equals([string]$targetUpdateManifest.version, $targetPackageVersion) -or
    -not (Test-Path -LiteralPath $targetFullArchive -PathType Leaf)) {
    throw 'Target update manifest is missing or does not describe the target package.'
}
$targetFullArchiveFile = Get-Item -LiteralPath $targetFullArchive
$targetFullArchiveHash = (Get-FileHash -LiteralPath $targetFullArchive -Algorithm SHA256).Hash.ToLowerInvariant()
if ([long]$targetUpdateManifest.size -ne [long]$targetFullArchiveFile.Length -or
    -not [StringComparer]::OrdinalIgnoreCase.Equals([string]$targetUpdateManifest.sha256, $targetFullArchiveHash)) {
    throw 'Target full archive size or SHA-256 does not match its update manifest.'
}

$baseFiles = @(Get-ZipFileIndex -Path $BaseArchive -RequiredRootDirectory $releaseRootName)
$baseMarker = Read-ZipEntryText -Path $BaseArchive -EntryName "$releaseRootName/autoplayer-release.json" | ConvertFrom-Json
if (-not [StringComparer]::Ordinal.Equals([string]$baseMarker.version, $baseVersion)) {
    throw 'Base archive marker version does not match the base manifest.'
}
$targetMarker = Get-Content -LiteralPath (Join-Path $TargetPackageDirectory 'autoplayer-release.json') -Raw | ConvertFrom-Json
if (-not [StringComparer]::Ordinal.Equals([string]$targetMarker.version, $targetPackageVersion)) {
    throw 'Target package marker version does not match the requested target version.'
}

$targetFiles = @(Get-DirectoryFileIndex -Path $TargetPackageDirectory)
$targetFilesByPath = @{}
foreach ($file in $targetFiles) { $targetFilesByPath[$file.Path.ToLowerInvariant()] = $file }
$targetChecksumPath = Join-Path $TargetPackageDirectory 'checksums.sha256'
$targetCatalog = Read-ChecksumCatalog -Path $targetChecksumPath
if ($targetFiles.Count -ne $targetCatalog.Count + 1) {
    throw 'Target checksums.sha256 does not describe the complete target package.'
}
foreach ($entry in $targetCatalog.Values) {
    $key = $entry.Path.ToLowerInvariant()
    if (-not $targetFilesByPath.ContainsKey($key) -or
        -not [StringComparer]::Ordinal.Equals($targetFilesByPath[$key].Path, $entry.Path) -or
        -not [StringComparer]::Ordinal.Equals($targetFilesByPath[$key].Sha256, $entry.Sha256)) {
        throw "Target checksum mismatch: $($entry.Path)"
    }
}

$baseFilesByPath = @{}
foreach ($file in $baseFiles) { $baseFilesByPath[$file.Path.ToLowerInvariant()] = $file }
$changed = @(
    $targetCatalog.Values |
        Where-Object {
            $key = $_.Path.ToLowerInvariant()
            -not $baseFilesByPath.ContainsKey($key) -or
            -not [StringComparer]::Ordinal.Equals($baseFilesByPath[$key].Sha256, $_.Sha256)
        } |
        Sort-Object Path
)
if ($changed.Count -eq 0) { throw 'Target package has no changed files relative to the base release.' }

$temporaryParent = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\', '/')
$temporaryRoot = Join-Path $temporaryParent ('LoopstructorAutoPlayerDelta-' + [Guid]::NewGuid().ToString('N'))
$deltaStagingRoot = Join-Path $temporaryRoot $deltaRootName
$deltaPayloadRoot = Join-Path $deltaStagingRoot 'files'
$deltaName = "Loopstructor.AutoPlayer-$baseVersion-to-$targetPackageVersion-$runtimeIdentifier.delta.zip"
$deltaPath = Join-Path $ReleaseDirectory $deltaName
try {
    New-Item -ItemType Directory -Path $deltaPayloadRoot -Force | Out-Null
    Copy-Item -LiteralPath $targetChecksumPath -Destination (Join-Path $deltaStagingRoot 'checksums.sha256')
    foreach ($entry in $changed) {
        $source = Join-Path $TargetPackageDirectory $entry.Path.Replace('/', '\')
        $destination = Join-Path $deltaPayloadRoot $entry.Path.Replace('/', '\')
        New-Item -ItemType Directory -Path ([System.IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination $destination
    }

    $sevenZipExecutable = Resolve-SevenZipExecutable -RequestedPath $SevenZipPath
    $listPath = Join-Path $temporaryRoot 'delta-zip-files.txt'
    New-MaximumCompressionZip `
        -SourceDirectory $deltaStagingRoot `
        -Destination $deltaPath `
        -SevenZipExecutable $sevenZipExecutable `
        -ListPath $listPath

    $deltaIndex = @(Get-ZipFileIndex -Path $deltaPath -RequiredRootDirectory $deltaRootName)
    $expectedDeltaFiles = @(Get-DirectoryFileIndex -Path $deltaStagingRoot)
    if ($deltaIndex.Count -ne $expectedDeltaFiles.Count) { throw 'Incremental ZIP file count mismatch.' }
    for ($index = 0; $index -lt $expectedDeltaFiles.Count; $index++) {
        if (-not [StringComparer]::Ordinal.Equals($deltaIndex[$index].Path, $expectedDeltaFiles[$index].Path) -or
            [long]$deltaIndex[$index].Length -ne [long]$expectedDeltaFiles[$index].Length -or
            -not [StringComparer]::Ordinal.Equals($deltaIndex[$index].Sha256, $expectedDeltaFiles[$index].Sha256)) {
            throw "Incremental ZIP differs from staging near '$($deltaIndex[$index].Path)'."
        }
    }

    $deltaFile = Get-Item -LiteralPath $deltaPath
    if ([long]$deltaFile.Length -ge [long](Get-Item -LiteralPath $targetFullArchive).Length) {
        Remove-Item -LiteralPath $deltaPath -Force
        if (Test-Path -LiteralPath "$deltaPath.sha256" -PathType Leaf) {
            Remove-Item -LiteralPath "$deltaPath.sha256" -Force
        }
        Write-Host 'Incremental package was not smaller than the full package; it was skipped.'
        return
    }

    $deltaHash = (Get-FileHash -LiteralPath $deltaPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Utf8NoBom -Path "$deltaPath.sha256" -Content "$deltaHash  $deltaName`n"
    $deltaDescriptor = [ordered]@{
        fromVersion = $baseVersion
        assetName = $deltaName
        sha256 = $deltaHash
        size = [long]$deltaFile.Length
    }
    $existing = @()
    if (($targetUpdateManifest.PSObject.Properties.Name -contains 'deltaAssets') -and
        $null -ne $targetUpdateManifest.deltaAssets) {
        $existing = @($targetUpdateManifest.deltaAssets | Where-Object {
            -not [StringComparer]::Ordinal.Equals([string]$_.fromVersion, $baseVersion) -and
            -not [StringComparer]::Ordinal.Equals([string]$_.assetName, $deltaName)
        })
    }
    $allDeltas = @($existing) + @($deltaDescriptor)
    if ($targetUpdateManifest.PSObject.Properties.Name -contains 'deltaAssets') {
        $targetUpdateManifest.deltaAssets = $allDeltas
    }
    else {
        $targetUpdateManifest | Add-Member -NotePropertyName deltaAssets -NotePropertyValue $allDeltas
    }
    Write-Utf8NoBom -Path $targetManifestPath -Content ($targetUpdateManifest | ConvertTo-Json -Depth 6)

    Write-Host "Incremental package: $deltaPath"
    Write-Host "Changed files: $($changed.Count) of $($targetCatalog.Count)"
    Write-Host "Incremental size: $($deltaFile.Length) bytes"
    Write-Host "Incremental SHA-256: $deltaHash"
}
catch {
    if (Test-Path -LiteralPath $deltaPath -PathType Leaf) {
        Remove-Item -LiteralPath $deltaPath -Force
    }
    if (Test-Path -LiteralPath "$deltaPath.sha256" -PathType Leaf) {
        Remove-Item -LiteralPath "$deltaPath.sha256" -Force
    }
    throw
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot -PathType Container) {
        $resolvedTemporary = [System.IO.Path]::GetFullPath($temporaryRoot)
        if ($resolvedTemporary.StartsWith($temporaryParent + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and
            [System.IO.Path]::GetFileName($resolvedTemporary).StartsWith('LoopstructorAutoPlayerDelta-', [StringComparison]::Ordinal)) {
            Remove-Item -LiteralPath $resolvedTemporary -Recurse -Force
        }
    }
}
