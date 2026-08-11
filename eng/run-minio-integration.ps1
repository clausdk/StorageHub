[CmdletBinding()]
param(
    [string] $FixtureRoot,
    [string] $DotNetArtifactsPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$minioRelease = 'RELEASE.2025-09-07T16-13-09Z'
$minioSha256 = 'AF709E6BA68488404E85ACDD22A3030D0F5E56A108D4B27D744F18CEB50861B4'
$minioUrl = "https://dl.min.io/server/minio/release/windows-amd64/archive/minio.$minioRelease"
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($FixtureRoot)) {
    $FixtureRoot = Join-Path $repositoryRoot 'artifacts\provider-fixtures\minio'
}
$fixtureRootPath = [IO.Path]::GetFullPath($FixtureRoot)
$cacheRoot = Join-Path $fixtureRootPath 'cache'
$runRoot = Join-Path $fixtureRootPath ("run-" + [Guid]::NewGuid().ToString('N'))
$dataRoot = Join-Path $runRoot 'data'
$binaryPath = Join-Path $cacheRoot "minio-$minioRelease.exe"
$downloadPath = Join-Path $cacheRoot ("download-" + [Guid]::NewGuid().ToString('N') + '.tmp')
$minioProcess = $null

$environmentNames = @(
    'STORAGEHUB_MINIO_ENDPOINT',
    'STORAGEHUB_MINIO_ACCESS_KEY',
    'STORAGEHUB_MINIO_SECRET_KEY',
    'STORAGEHUB_MINIO_BUCKET',
    'STORAGEHUB_REQUIRE_MINIO'
)
$originalEnvironment = @{}
foreach ($name in $environmentNames) {
    $originalEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

function Get-LoopbackPorts {
    $first = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $second = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    try {
        $first.Start()
        $second.Start()
        return @(
            ([Net.IPEndPoint] $first.LocalEndpoint).Port,
            ([Net.IPEndPoint] $second.LocalEndpoint).Port
        )
    }
    finally {
        $second.Stop()
        $first.Stop()
    }
}

function Remove-VerifiedRunRoot {
    if (-not (Test-Path -LiteralPath $runRoot)) {
        return
    }

    $resolvedRunRoot = [IO.Path]::GetFullPath($runRoot)
    $resolvedParent = [IO.Path]::GetFullPath((Split-Path -Parent $resolvedRunRoot))
    if (-not [string]::Equals(
            $resolvedParent.TrimEnd('\'),
            $fixtureRootPath.TrimEnd('\'),
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove unexpected MinIO run directory '$resolvedRunRoot'."
    }

    Remove-Item -LiteralPath $resolvedRunRoot -Recurse -Force
}

try {
    [IO.Directory]::CreateDirectory($cacheRoot) | Out-Null
    [IO.Directory]::CreateDirectory($dataRoot) | Out-Null

    if (-not (Test-Path -LiteralPath $binaryPath -PathType Leaf) -or
        (Get-FileHash -LiteralPath $binaryPath -Algorithm SHA256).Hash -cne $minioSha256) {
        Write-Host "Downloading pinned MinIO $minioRelease fixture."
        Invoke-WebRequest -Uri $minioUrl -OutFile $downloadPath
        $downloadHash = (Get-FileHash -LiteralPath $downloadPath -Algorithm SHA256).Hash
        if ($downloadHash -cne $minioSha256) {
            throw "The MinIO fixture SHA-256 did not match the reviewed release."
        }

        Move-Item -LiteralPath $downloadPath -Destination $binaryPath -Force
    }

    $cachedHash = (Get-FileHash -LiteralPath $binaryPath -Algorithm SHA256).Hash
    if ($cachedHash -cne $minioSha256) {
        throw "The cached MinIO fixture SHA-256 is invalid."
    }

    $ports = Get-LoopbackPorts
    $apiPort = $ports[0]
    $consolePort = $ports[1]
    $endpoint = "http://127.0.0.1:$apiPort/"
    $accessKey = "storagehub" + [Guid]::NewGuid().ToString('N')
    $secretBytes = New-Object byte[] 32
    $random = [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $random.GetBytes($secretBytes)
    }
    finally {
        $random.Dispose()
    }
    $secretKey = [BitConverter]::ToString($secretBytes).Replace('-', '')
    $bucket = "storagehub-" + [Guid]::NewGuid().ToString('N')

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $binaryPath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.Arguments =
        "server --address 127.0.0.1:$apiPort --console-address 127.0.0.1:$consolePort `"$dataRoot`""
    $startInfo.EnvironmentVariables['MINIO_ROOT_USER'] = $accessKey
    $startInfo.EnvironmentVariables['MINIO_ROOT_PASSWORD'] = $secretKey
    $startInfo.EnvironmentVariables['MINIO_BROWSER'] = 'off'

    $minioProcess = [Diagnostics.Process]::new()
    $minioProcess.StartInfo = $startInfo
    if (-not $minioProcess.Start()) {
        throw 'The MinIO fixture process did not start.'
    }

    $readyUri = $endpoint + 'minio/health/ready'
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(60)
    $ready = $false
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if ($minioProcess.HasExited) {
            throw "The MinIO fixture exited before becoming ready (exit code $($minioProcess.ExitCode))."
        }

        try {
            $response = Invoke-WebRequest -Uri $readyUri -TimeoutSec 2 -UseBasicParsing
            if ($response.StatusCode -eq 200) {
                $ready = $true
                break
            }
        }
        catch {
            # A connection refusal is expected while the disposable server starts.
        }
        Start-Sleep -Milliseconds 250
    }
    if (-not $ready) {
        throw 'The MinIO fixture did not become ready within 60 seconds.'
    }

    [Environment]::SetEnvironmentVariable('STORAGEHUB_MINIO_ENDPOINT', $endpoint, 'Process')
    [Environment]::SetEnvironmentVariable('STORAGEHUB_MINIO_ACCESS_KEY', $accessKey, 'Process')
    [Environment]::SetEnvironmentVariable('STORAGEHUB_MINIO_SECRET_KEY', $secretKey, 'Process')
    [Environment]::SetEnvironmentVariable('STORAGEHUB_MINIO_BUCKET', $bucket, 'Process')
    [Environment]::SetEnvironmentVariable('STORAGEHUB_REQUIRE_MINIO', '1', 'Process')

    $testProject = Join-Path $repositoryRoot `
        'tests\StorageHub.Storage.CodeLogic.Tests\StorageHub.Storage.CodeLogic.Tests.csproj'
    $arguments = @(
        'test',
        $testProject,
        '--configuration',
        'Release',
        '--no-build',
        '--no-restore',
        '--filter',
        'Category=ProviderIntegration',
        '--logger',
        'trx;LogFileName=minio-s3.trx'
    )
    if (-not [string]::IsNullOrWhiteSpace($DotNetArtifactsPath)) {
        $arguments += @('--artifacts-path', [IO.Path]::GetFullPath($DotNetArtifactsPath))
    }
    Write-Host "Running the S3 conformance suite against disposable MinIO $minioRelease on loopback."
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "The MinIO/S3 integration suite failed with exit code $LASTEXITCODE."
    }
}
finally {
    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable($name, $originalEnvironment[$name], 'Process')
    }

    if ($null -ne $minioProcess) {
        try {
            if (-not $minioProcess.HasExited) {
                $minioProcess.Kill()
                $minioProcess.WaitForExit(10000) | Out-Null
            }
        }
        finally {
            $minioProcess.Dispose()
        }
    }

    if (Test-Path -LiteralPath $downloadPath) {
        Remove-Item -LiteralPath $downloadPath -Force
    }
    Remove-VerifiedRunRoot
}
