[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$NoRestore,

    [switch]$NoBuild
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

$resultsDirectory = Join-Path (Get-RepositoryRoot) 'artifacts\TestResults'
New-Item -ItemType Directory -Path $resultsDirectory -Force | Out-Null

$testArguments = @(
    'test',
    $script:SolutionPath,
    '--configuration', $Configuration,
    '--no-restore',
    '--logger', 'trx',
    '--results-directory', $resultsDirectory,
    '--nologo'
)

if ($NoBuild) {
    $testArguments += '--no-build'
}

Invoke-DotNet -Arguments $testArguments

$verificationArguments = @(
    'run',
    '--project', (Join-Path (Get-RepositoryRoot) 'src\Loopstructor.AutoPlayer.Updater\Verification\Verification.csproj'),
    '--configuration', $Configuration,
    '--no-restore'
)
if ($NoBuild) {
    $verificationArguments += '--no-build'
}

Invoke-DotNet -Arguments $verificationArguments

Invoke-Pnpm -Arguments @('typecheck')
Invoke-Pnpm -Arguments @('lint')
Invoke-Pnpm -Arguments @('test')
