[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$Version,

    [switch]$NoRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'common.ps1')
Assert-SolutionExists

if (-not $NoRestore) {
    Invoke-DotNet -Arguments @(
        'restore',
        $script:SolutionPath,
        '--configfile', $script:NuGetConfigPath,
        '--verbosity', 'minimal'
    )
}

$buildArguments = @(
    'build',
    $script:SolutionPath,
    '--configuration', $Configuration,
    '--no-restore',
    '--nologo'
)

if (-not [string]::IsNullOrWhiteSpace($Version)) {
    $buildArguments += "-p:Version=$Version"
}

Invoke-DotNet -Arguments $buildArguments
