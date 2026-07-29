[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'This bootstrap installs the pinned Windows x64 .NET SDK and must run on Windows.'
}

[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$globalJsonPath = Join-Path $repositoryRoot 'global.json'
$dotNetRoot = Join-Path $repositoryRoot '.dotnet'
$dotNetExecutable = Join-Path $dotNetRoot 'dotnet.exe'
$toolsRoot = Join-Path $repositoryRoot '.tools'

$sdkVersion = [string]((Get-Content -LiteralPath $globalJsonPath -Raw | ConvertFrom-Json).sdk.version)
if ($sdkVersion -ne '8.0.423') {
    throw "bootstrap.ps1 expects SDK 8.0.423, but global.json requests '$sdkVersion'. Update both pins together."
}

$archiveName = "dotnet-sdk-$sdkVersion-win-x64.zip"
$archiveUrl = "https://builds.dotnet.microsoft.com/dotnet/Sdk/$sdkVersion/$archiveName"
$archiveSha512 = '063fcc35c136277e6fd767c66579f3b92db22a078a7f0c7177b6af1edb2c9afae1613f6cfdc01acf7421773d9ac77f0ef73a7fd8b37f469e7e3505e5c1361ba0'
$archivePath = Join-Path $toolsRoot $archiveName

function Get-InstalledSdkVersion {
    if (-not (Test-Path -LiteralPath $dotNetExecutable -PathType Leaf)) {
        return $null
    }

    $version = (& $dotNetExecutable --version).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "The existing local dotnet executable could not be started: $dotNetExecutable"
    }

    return $version
}

$installedVersion = Get-InstalledSdkVersion
if ($installedVersion) {
    if ($installedVersion -ne $sdkVersion) {
        throw "The repository-local SDK is '$installedVersion', but '$sdkVersion' is required. Remove '$dotNetRoot' and run bootstrap again."
    }

    Write-Host ".NET SDK $sdkVersion is already installed in $dotNetRoot"
    return
}

if (Test-Path -LiteralPath $dotNetRoot) {
    $existingEntries = @(Get-ChildItem -LiteralPath $dotNetRoot -Force)
    if ($existingEntries.Count -gt 0) {
        throw "The local SDK directory exists but is incomplete: $dotNetRoot. Remove it and run bootstrap again."
    }
}

New-Item -ItemType Directory -Path $toolsRoot -Force | Out-Null

$archiveIsValid = $false
if (Test-Path -LiteralPath $archivePath -PathType Leaf) {
    $archiveIsValid = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA512).Hash.ToLowerInvariant() -eq $archiveSha512
}

if (-not $archiveIsValid) {
    $partialPath = "$archivePath.partial"
    if (Test-Path -LiteralPath $partialPath) {
        Remove-Item -LiteralPath $partialPath -Force
    }

    Write-Host "Downloading .NET SDK $sdkVersion..."
    Invoke-WebRequest -UseBasicParsing -Uri $archiveUrl -OutFile $partialPath

    $downloadedHash = (Get-FileHash -LiteralPath $partialPath -Algorithm SHA512).Hash.ToLowerInvariant()
    if ($downloadedHash -ne $archiveSha512) {
        Remove-Item -LiteralPath $partialPath -Force
        throw "The .NET SDK archive failed SHA-512 verification. Expected $archiveSha512, got $downloadedHash."
    }

    Move-Item -LiteralPath $partialPath -Destination $archivePath -Force
}

$stagingRoot = Join-Path $repositoryRoot ('.dotnet-staging-' + [Guid]::NewGuid().ToString('N'))
try {
    Write-Host "Extracting .NET SDK $sdkVersion..."
    Expand-Archive -LiteralPath $archivePath -DestinationPath $stagingRoot

    $stagedDotNet = Join-Path $stagingRoot 'dotnet.exe'
    $stagedVersion = (& $stagedDotNet --version).Trim()
    if ($LASTEXITCODE -ne 0 -or $stagedVersion -ne $sdkVersion) {
        throw "The extracted SDK reported '$stagedVersion' instead of '$sdkVersion'."
    }

    if (Test-Path -LiteralPath $dotNetRoot) {
        Remove-Item -LiteralPath $dotNetRoot -Force
    }
    Move-Item -LiteralPath $stagingRoot -Destination $dotNetRoot
}
finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }
}

Write-Host ".NET SDK $sdkVersion installed in $dotNetRoot"
