Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:RepositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$script:SolutionPath = Join-Path $script:RepositoryRoot 'Loopstructor.AutoPlayer.sln'
$script:NuGetConfigPath = Join-Path $script:RepositoryRoot 'NuGet.config'
$script:GlobalJsonPath = Join-Path $script:RepositoryRoot 'global.json'

function Get-RepositoryRoot {
    return $script:RepositoryRoot
}

function Get-ExpectedSdkVersion {
    if (-not (Test-Path -LiteralPath $script:GlobalJsonPath -PathType Leaf)) {
        throw "Missing SDK configuration: $script:GlobalJsonPath"
    }

    $configuration = Get-Content -LiteralPath $script:GlobalJsonPath -Raw | ConvertFrom-Json
    $version = [string]$configuration.sdk.version
    if ([string]::IsNullOrWhiteSpace($version)) {
        throw "global.json does not define sdk.version."
    }

    return $version
}

function Set-DotNetEnvironment {
    param(
        [Parameter(Mandatory = $true)]
        [string]$DotNetRoot
    )

    $env:DOTNET_ROOT = $DotNetRoot
    $pathEntries = @($env:PATH -split ';')
    if ($pathEntries -notcontains $DotNetRoot) {
        $env:PATH = "$DotNetRoot;$env:PATH"
    }

    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
}

function Get-LocalDotNet {
    $dotNetRoot = Join-Path $script:RepositoryRoot '.dotnet'
    $dotNet = Join-Path $dotNetRoot 'dotnet.exe'

    if (-not (Test-Path -LiteralPath $dotNet -PathType Leaf)) {
        & (Join-Path $PSScriptRoot 'bootstrap.ps1')
    }

    if (-not (Test-Path -LiteralPath $dotNet -PathType Leaf)) {
        throw "The local .NET SDK bootstrap did not create $dotNet."
    }

    Set-DotNetEnvironment -DotNetRoot $dotNetRoot
    $actualVersion = (& $dotNet --version).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to execute the local .NET SDK at $dotNet."
    }

    $expectedVersion = Get-ExpectedSdkVersion
    if ($actualVersion -ne $expectedVersion) {
        throw "Local .NET SDK version '$actualVersion' does not match the pinned version '$expectedVersion'."
    }

    return $dotNet
}

function Invoke-DotNet {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    $dotNet = Get-LocalDotNet
    & $dotNet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Assert-SolutionExists {
    if (-not (Test-Path -LiteralPath $script:SolutionPath -PathType Leaf)) {
        throw "Solution not found: $script:SolutionPath"
    }
}
