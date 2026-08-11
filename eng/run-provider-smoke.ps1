[CmdletBinding()]
param(
    [string] $FixtureRoot,
    [string] $DotNetArtifactsPath,
    [switch] $NoBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($FixtureRoot)) {
    $FixtureRoot = Join-Path $repositoryRoot 'artifacts\provider-fixtures'
}
$fixtureRootPath = [IO.Path]::GetFullPath($FixtureRoot)

Push-Location $repositoryRoot
try {
    if (-not $NoBuild) {
        Write-Host 'Building StorageHub provider integration tests in Release mode.'
        & dotnet build StorageHub.slnx --configuration Release --no-restore
        if ($LASTEXITCODE -ne 0) {
            throw "The Release build failed with exit code $LASTEXITCODE."
        }
    }

    $common = @{}
    if (-not [string]::IsNullOrWhiteSpace($DotNetArtifactsPath)) {
        $common.DotNetArtifactsPath = [IO.Path]::GetFullPath($DotNetArtifactsPath)
    }

    & (Join-Path $PSScriptRoot 'run-minio-integration.ps1') `
        -FixtureRoot (Join-Path $fixtureRootPath 'minio') @common
    & (Join-Path $PSScriptRoot 'run-ftp-integration.ps1') `
        -FixtureRoot (Join-Path $fixtureRootPath 'ftp') @common
    & (Join-Path $PSScriptRoot 'run-sftp-integration.ps1') `
        -FixtureRoot (Join-Path $fixtureRootPath 'sftp') @common

    Write-Host 'Provider smoke suite passed: S3, FTP/FTPS, SFTP, and provider-to-local transfers.'
}
finally {
    Pop-Location
}
