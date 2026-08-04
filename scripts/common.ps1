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

function ConvertTo-CanonicalSemanticVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    $pattern = '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-((?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$'
    $match = [regex]::Match($Value, $pattern, [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
    [int]$major = 0
    [int]$minor = 0
    [int]$patch = 0
    if (-not $match.Success -or
        -not [int]::TryParse($match.Groups[1].Value, [ref]$major) -or
        -not [int]::TryParse($match.Groups[2].Value, [ref]$minor) -or
        -not [int]::TryParse($match.Groups[3].Value, [ref]$patch)) {
        throw "Version '$Value' is not a canonical semantic version."
    }

    $preRelease = if ($match.Groups[4].Success) {
        @($match.Groups[4].Value -split '\.')
    }
    else {
        @()
    }
    return [pscustomobject]@{
        Text = $Value
        Major = $major
        Minor = $minor
        Patch = $patch
        PreRelease = $preRelease
    }
}

function Test-CanonicalSemanticVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    try {
        $null = ConvertTo-CanonicalSemanticVersion -Value $Value
        return $true
    }
    catch {
        return $false
    }
}

function Compare-CanonicalSemanticVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Left,

        [Parameter(Mandatory = $true)]
        [string]$Right
    )

    $leftVersion = ConvertTo-CanonicalSemanticVersion -Value $Left
    $rightVersion = ConvertTo-CanonicalSemanticVersion -Value $Right
    foreach ($property in @('Major', 'Minor', 'Patch')) {
        if ($leftVersion.$property -lt $rightVersion.$property) { return -1 }
        if ($leftVersion.$property -gt $rightVersion.$property) { return 1 }
    }

    $leftPreRelease = @($leftVersion.PreRelease)
    $rightPreRelease = @($rightVersion.PreRelease)
    if ($leftPreRelease.Count -eq 0 -and $rightPreRelease.Count -eq 0) { return 0 }
    if ($leftPreRelease.Count -eq 0) { return 1 }
    if ($rightPreRelease.Count -eq 0) { return -1 }

    $count = [Math]::Max($leftPreRelease.Count, $rightPreRelease.Count)
    for ($index = 0; $index -lt $count; $index++) {
        if ($index -ge $leftPreRelease.Count) { return -1 }
        if ($index -ge $rightPreRelease.Count) { return 1 }
        $leftIdentifier = [string]$leftPreRelease[$index]
        $rightIdentifier = [string]$rightPreRelease[$index]
        $leftNumeric = $leftIdentifier -match '^(0|[1-9][0-9]*)$'
        $rightNumeric = $rightIdentifier -match '^(0|[1-9][0-9]*)$'
        if ($leftNumeric -and $rightNumeric) {
            $leftNumber = [System.Numerics.BigInteger]::Parse($leftIdentifier)
            $rightNumber = [System.Numerics.BigInteger]::Parse($rightIdentifier)
            $comparison = $leftNumber.CompareTo($rightNumber)
        }
        elseif ($leftNumeric) {
            $comparison = -1
        }
        elseif ($rightNumeric) {
            $comparison = 1
        }
        else {
            $comparison = [StringComparer]::Ordinal.Compare($leftIdentifier, $rightIdentifier)
        }
        if ($comparison -lt 0) { return -1 }
        if ($comparison -gt 0) { return 1 }
    }

    return 0
}
