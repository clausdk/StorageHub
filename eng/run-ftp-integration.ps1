[CmdletBinding()]
param(
    [string] $FixtureRoot,
    [string] $DotNetArtifactsPath,
    [string] $CLStorageProjectPath,
    [string] $CodeLogicLibsRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$pyftpdlibVersion = '2.2.0'
$pyftpdlibSha256 = '4BA0642078792DF63DD3B2E9C8F838F2A3ECF428C7518D5921C0530D53512ACF'
$pyftpdlibUrl = "https://files.pythonhosted.org/packages/source/p/pyftpdlib/pyftpdlib-$pyftpdlibVersion.tar.gz"
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($FixtureRoot)) {
    $FixtureRoot = Join-Path $repositoryRoot 'artifacts\provider-fixtures\ftp'
}
$fixtureRootPath = [IO.Path]::GetFullPath($FixtureRoot)
if ($fixtureRootPath.Contains('"')) {
    throw 'The FTP fixture root cannot contain a quotation mark.'
}
$cacheRoot = Join-Path $fixtureRootPath 'cache'
$runRoot = Join-Path $fixtureRootPath ("run-" + [Guid]::NewGuid().ToString('N'))
$dependencyRoot = Join-Path $runRoot 'dependencies'
$sourceContainer = Join-Path $runRoot 'source'
$sourceRoot = Join-Path $sourceContainer "pyftpdlib-$pyftpdlibVersion"
$certificateRoot = Join-Path $runRoot 'certificates'
$archivePath = Join-Path $cacheRoot "pyftpdlib-$pyftpdlibVersion.tar.gz"
$downloadPath = Join-Path $cacheRoot ("download-" + [Guid]::NewGuid().ToString('N') + '.tmp')
$fixtureProcesses = New-Object 'System.Collections.Generic.List[System.Diagnostics.Process]'

$environmentNames = @(
    'STORAGEHUB_FTP_USERNAME',
    'STORAGEHUB_FTP_PASSWORD',
    'STORAGEHUB_FTP_CLIENT_PFX_PASSWORD',
    'STORAGEHUB_FTP_SERVER_KEY_PASSWORD',
    'STORAGEHUB_FTP_PLAIN_PORT',
    'STORAGEHUB_FTP_EXPLICIT_PORT',
    'STORAGEHUB_FTP_IMPLICIT_PORT',
    'STORAGEHUB_FTP_MTLS_PORT',
    'STORAGEHUB_FTP_SERVER_SHA256',
    'STORAGEHUB_FTP_CLIENT_PFX_PATH',
    'STORAGEHUB_REQUIRE_FTP',
    'PYTHONPATH'
)
$originalEnvironment = @{}
foreach ($name in $environmentNames) {
    $originalEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

function New-RandomHex([int] $byteCount) {
    $bytes = New-Object byte[] $byteCount
    $random = [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $random.GetBytes($bytes)
    }
    finally {
        $random.Dispose()
    }
    return [BitConverter]::ToString($bytes).Replace('-', '')
}

function Get-LoopbackPorts([int] $count) {
    $listeners = New-Object 'System.Collections.Generic.List[System.Net.Sockets.TcpListener]'
    try {
        $ports = @()
        for ($index = 0; $index -lt $count; $index++) {
            $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
            $listener.Start()
            $listeners.Add($listener)
            $ports += ([Net.IPEndPoint] $listener.LocalEndpoint).Port
        }
        return $ports
    }
    finally {
        foreach ($listener in $listeners) {
            $listener.Stop()
        }
    }
}

function Start-FtpFixtureProcess(
    [string] $mode,
    [int] $port,
    [int[]] $passivePorts,
    [string] $root,
    [string] $readyFile,
    [bool] $requireClientCertificate) {
    $serverScript = Join-Path $repositoryRoot 'eng\fixtures\ftp_fixture_server.py'
    $arguments =
        "-S `"$serverScript`" --mode $mode --port $port --passive-ports $($passivePorts -join ',') " +
        "--root `"$root`" --ready-file `"$readyFile`""
    if ($mode -ne 'plain') {
        $arguments +=
            " --certificate `"$(Join-Path $certificateRoot 'server.pem')`"" +
            " --private-key `"$(Join-Path $certificateRoot 'server-key.pem')`""
    }
    if ($requireClientCertificate) {
        $arguments +=
            " --require-client-certificate --client-ca `"$(Join-Path $certificateRoot 'ca.pem')`""
    }

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $pythonPath
    $startInfo.Arguments = $arguments
    $startInfo.WorkingDirectory = $repositoryRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.EnvironmentVariables['PYTHONPATH'] = "$dependencyRoot;$sourceRoot"
    $startInfo.EnvironmentVariables['PYTHONDONTWRITEBYTECODE'] = '1'
    $startInfo.EnvironmentVariables['STORAGEHUB_FTP_USERNAME'] = $username
    $startInfo.EnvironmentVariables['STORAGEHUB_FTP_PASSWORD'] = $password
    $startInfo.EnvironmentVariables['STORAGEHUB_FTP_SERVER_KEY_PASSWORD'] = $serverKeyPassword

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) {
        throw "The $mode FTP fixture process did not start."
    }
    $process.BeginOutputReadLine()
    $process.BeginErrorReadLine()
    $fixtureProcesses.Add($process)
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
        throw "Refusing to remove unexpected FTP run directory '$resolvedRunRoot'."
    }

    Remove-Item -LiteralPath $resolvedRunRoot -Recurse -Force
}

try {
    $pythonCommand = Get-Command python -ErrorAction Stop
    $pythonPath = $pythonCommand.Source
    $pythonVersion = (& $pythonPath --version).Trim()
    if ($LASTEXITCODE -ne 0 -or $pythonVersion -notmatch '^Python 3\.12\.[0-9]+$') {
        throw "The FTP fixture requires CPython 3.12; resolved '$pythonVersion'."
    }

    [IO.Directory]::CreateDirectory($cacheRoot) | Out-Null
    [IO.Directory]::CreateDirectory($dependencyRoot) | Out-Null
    [IO.Directory]::CreateDirectory($sourceContainer) | Out-Null
    [IO.Directory]::CreateDirectory($certificateRoot) | Out-Null

    if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf) -or
        (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -cne $pyftpdlibSha256) {
        Write-Host "Downloading pinned pyftpdlib $pyftpdlibVersion fixture source."
        Invoke-WebRequest -Uri $pyftpdlibUrl -OutFile $downloadPath -UseBasicParsing
        $downloadHash = (Get-FileHash -LiteralPath $downloadPath -Algorithm SHA256).Hash
        if ($downloadHash -cne $pyftpdlibSha256) {
            throw 'The pyftpdlib fixture SHA-256 did not match the reviewed release.'
        }
        Move-Item -LiteralPath $downloadPath -Destination $archivePath -Force
    }
    if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -cne $pyftpdlibSha256) {
        throw 'The cached pyftpdlib fixture SHA-256 is invalid.'
    }

    $requirements = Join-Path $repositoryRoot 'eng\fixtures\ftp-requirements.txt'
    & $pythonPath -m pip install `
        --disable-pip-version-check `
        --no-compile `
        --no-deps `
        --target $dependencyRoot `
        -r $requirements
    if ($LASTEXITCODE -ne 0) {
        throw "The hash-locked FTP fixture dependencies failed to install."
    }

    & $pythonPath -S (Join-Path $repositoryRoot 'eng\fixtures\extract_pinned_archive.py') `
        --archive $archivePath `
        --output $sourceContainer
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $sourceRoot -PathType Container)) {
        throw 'The pinned pyftpdlib source could not be extracted safely.'
    }

    $username = "storagehub" + [Guid]::NewGuid().ToString('N')
    $password = New-RandomHex 32
    $pfxPassword = New-RandomHex 32
    $serverKeyPassword = New-RandomHex 32
    [Environment]::SetEnvironmentVariable(
        'STORAGEHUB_FTP_CLIENT_PFX_PASSWORD', $pfxPassword, 'Process')
    [Environment]::SetEnvironmentVariable(
        'STORAGEHUB_FTP_SERVER_KEY_PASSWORD', $serverKeyPassword, 'Process')
    $env:PYTHONPATH = "$dependencyRoot;$sourceRoot"
    & $pythonPath -S (Join-Path $repositoryRoot 'eng\fixtures\generate_ftp_certificates.py') `
        --output $certificateRoot
    if ($LASTEXITCODE -ne 0) {
        throw 'The FTP fixture certificates could not be generated.'
    }

    $ports = Get-LoopbackPorts 36
    $plainPort = $ports[0]
    $explicitPort = $ports[1]
    $implicitPort = $ports[2]
    $mtlsPort = $ports[3]
    $serverDefinitions = @(
        @{ Mode = 'plain'; Port = $plainPort; Passive = [int[]]$ports[4..11]; Mutual = $false },
        @{ Mode = 'explicit'; Port = $explicitPort; Passive = [int[]]$ports[12..19]; Mutual = $false },
        @{ Mode = 'implicit'; Port = $implicitPort; Passive = [int[]]$ports[20..27]; Mutual = $false },
        @{ Mode = 'explicit'; Port = $mtlsPort; Passive = [int[]]$ports[28..35]; Mutual = $true }
    )
    $readyFiles = @()
    foreach ($definition in $serverDefinitions) {
        $identity = "server-$($definition.Port)"
        $serverRoot = Join-Path $runRoot $identity
        $readyFile = Join-Path $runRoot "$identity.ready"
        [IO.Directory]::CreateDirectory((Join-Path $serverRoot 'mounted')) | Out-Null
        $readyFiles += $readyFile
        Start-FtpFixtureProcess `
            -mode $definition.Mode `
            -port $definition.Port `
            -passivePorts $definition.Passive `
            -root $serverRoot `
            -readyFile $readyFile `
            -requireClientCertificate $definition.Mutual
    }

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $exited = @($fixtureProcesses | Where-Object { $_.HasExited })
        if ($exited.Count -gt 0) {
            throw 'An FTP fixture process exited before all endpoints became ready.'
        }
        if (@($readyFiles | Where-Object { -not (Test-Path -LiteralPath $_ -PathType Leaf) }).Count -eq 0) {
            break
        }
        Start-Sleep -Milliseconds 200
    }
    if (@($readyFiles | Where-Object { -not (Test-Path -LiteralPath $_ -PathType Leaf) }).Count -ne 0) {
        throw 'The FTP fixture endpoints did not become ready within 30 seconds.'
    }

    $serverFingerprint = (Get-Content -LiteralPath (Join-Path $certificateRoot 'server.sha256') -Raw).Trim()
    $clientPfxPath = Join-Path $certificateRoot 'client.pfx'
    [Environment]::SetEnvironmentVariable('STORAGEHUB_FTP_USERNAME', $username, 'Process')
    [Environment]::SetEnvironmentVariable('STORAGEHUB_FTP_PASSWORD', $password, 'Process')
    [Environment]::SetEnvironmentVariable('STORAGEHUB_FTP_PLAIN_PORT', "$plainPort", 'Process')
    [Environment]::SetEnvironmentVariable('STORAGEHUB_FTP_EXPLICIT_PORT', "$explicitPort", 'Process')
    [Environment]::SetEnvironmentVariable('STORAGEHUB_FTP_IMPLICIT_PORT', "$implicitPort", 'Process')
    [Environment]::SetEnvironmentVariable('STORAGEHUB_FTP_MTLS_PORT', "$mtlsPort", 'Process')
    [Environment]::SetEnvironmentVariable('STORAGEHUB_FTP_SERVER_SHA256', $serverFingerprint, 'Process')
    [Environment]::SetEnvironmentVariable('STORAGEHUB_FTP_CLIENT_PFX_PATH', $clientPfxPath, 'Process')
    [Environment]::SetEnvironmentVariable('STORAGEHUB_REQUIRE_FTP', '1', 'Process')

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
        'Category=FtpProviderIntegration',
        '--logger',
        'trx;LogFileName=ftp-ftps.trx'
    )
    if (-not [string]::IsNullOrWhiteSpace($DotNetArtifactsPath)) {
        $arguments += @('--artifacts-path', [IO.Path]::GetFullPath($DotNetArtifactsPath))
    }
    if (-not [string]::IsNullOrWhiteSpace($CLStorageProjectPath)) {
        $arguments += "-p:CLStorageProjectPath=$([IO.Path]::GetFullPath($CLStorageProjectPath))"
    }
    if (-not [string]::IsNullOrWhiteSpace($CodeLogicLibsRoot)) {
        $arguments += "-p:CodeLogicLibsRoot=$([IO.Path]::GetFullPath($CodeLogicLibsRoot))"
    }

    Write-Host 'Running FTP, explicit/implicit FTPS, and mutual-TLS conformance on loopback.'
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "The FTP/FTPS integration suite failed with exit code $LASTEXITCODE."
    }
}
finally {
    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable($name, $originalEnvironment[$name], 'Process')
    }

    foreach ($process in $fixtureProcesses) {
        try {
            if (-not $process.HasExited) {
                $process.Kill()
                $process.WaitForExit(10000) | Out-Null
            }
        }
        finally {
            $process.Dispose()
        }
    }

    if (Test-Path -LiteralPath $downloadPath) {
        Remove-Item -LiteralPath $downloadPath -Force
    }
    Remove-VerifiedRunRoot
}
