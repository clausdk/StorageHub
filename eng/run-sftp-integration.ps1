[CmdletBinding()]
param(
    [string] $FixtureRoot,
    [string] $DotNetArtifactsPath,
    [string] $CLStorageProjectPath,
    [string] $CodeLogicLibsRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($FixtureRoot)) {
    $FixtureRoot = Join-Path $repositoryRoot 'artifacts\provider-fixtures\sftp'
}
$fixtureRootPath = [IO.Path]::GetFullPath($FixtureRoot)
if ($fixtureRootPath.Contains('"')) {
    throw 'The SFTP fixture root cannot contain a quotation mark.'
}
$runRoot = Join-Path $fixtureRootPath ("run-" + [Guid]::NewGuid().ToString('N'))
$dependencyRoot = Join-Path $runRoot 'dependencies'
$keyRoot = Join-Path $runRoot 'keys'
$fixtureProcesses = New-Object 'System.Collections.Generic.List[System.Diagnostics.Process]'

$environmentNames = @(
    'STORAGEHUB_SFTP_USERNAME',
    'STORAGEHUB_SFTP_PASSWORD',
    'STORAGEHUB_SFTP_HOST_KEY_PASSPHRASE',
    'STORAGEHUB_SFTP_CLIENT_KEY_PASSPHRASE',
    'STORAGEHUB_SFTP_ALTERNATE_KEY_PASSPHRASE',
    'STORAGEHUB_SFTP_PASSWORD_PORT',
    'STORAGEHUB_SFTP_PRIVATE_KEY_PORT',
    'STORAGEHUB_SFTP_ROTATED_PORT',
    'STORAGEHUB_SFTP_HOST_SHA256',
    'STORAGEHUB_SFTP_ROTATED_HOST_SHA256',
    'STORAGEHUB_SFTP_CLIENT_KEY_PATH',
    'STORAGEHUB_SFTP_ALTERNATE_KEY_PATH',
    'STORAGEHUB_REQUIRE_SFTP',
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

function Start-SftpFixtureProcess(
    [string] $mode,
    [int] $port,
    [string] $root,
    [string] $readyFile,
    [string] $hostKey) {
    $serverScript = Join-Path $repositoryRoot 'eng\fixtures\sftp_fixture_server.py'
    $authorizedKey = Join-Path $keyRoot 'client.pub'
    $arguments =
        "-S `"$serverScript`" --mode $mode --port $port --root `"$root`" " +
        "--ready-file `"$readyFile`" --host-key `"$hostKey`" " +
        "--authorized-key `"$authorizedKey`""

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $pythonPath
    $startInfo.Arguments = $arguments
    $startInfo.WorkingDirectory = $repositoryRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.EnvironmentVariables['PYTHONPATH'] = $dependencyRoot
    $startInfo.EnvironmentVariables['PYTHONDONTWRITEBYTECODE'] = '1'
    $startInfo.EnvironmentVariables['STORAGEHUB_SFTP_USERNAME'] = $username
    $startInfo.EnvironmentVariables['STORAGEHUB_SFTP_PASSWORD'] = $password
    $startInfo.EnvironmentVariables['STORAGEHUB_SFTP_HOST_KEY_PASSPHRASE'] = $hostKeyPassphrase

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) {
        throw "The $mode SFTP fixture process did not start."
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
        throw "Refusing to remove unexpected SFTP run directory '$resolvedRunRoot'."
    }

    Remove-Item -LiteralPath $resolvedRunRoot -Recurse -Force
}

try {
    $pythonCommand = Get-Command python -ErrorAction Stop
    $pythonPath = $pythonCommand.Source
    $pythonVersion = (& $pythonPath --version).Trim()
    if ($LASTEXITCODE -ne 0 -or $pythonVersion -notmatch '^Python 3\.12\.[0-9]+$') {
        throw "The SFTP fixture requires CPython 3.12; resolved '$pythonVersion'."
    }

    [IO.Directory]::CreateDirectory($dependencyRoot) | Out-Null
    [IO.Directory]::CreateDirectory($keyRoot) | Out-Null

    $requirements = Join-Path $repositoryRoot 'eng\fixtures\sftp-requirements.txt'
    & $pythonPath -m pip install `
        --disable-pip-version-check `
        --no-compile `
        --no-deps `
        --target $dependencyRoot `
        -r $requirements
    if ($LASTEXITCODE -ne 0) {
        throw 'The hash-locked SFTP fixture dependencies failed to install.'
    }

    $username = "storagehub" + [Guid]::NewGuid().ToString('N')
    $password = New-RandomHex 32
    $hostKeyPassphrase = New-RandomHex 32
    $clientKeyPassphrase = New-RandomHex 32
    $alternateKeyPassphrase = New-RandomHex 32
    [Environment]::SetEnvironmentVariable(
        'STORAGEHUB_SFTP_HOST_KEY_PASSPHRASE', $hostKeyPassphrase, 'Process')
    [Environment]::SetEnvironmentVariable(
        'STORAGEHUB_SFTP_CLIENT_KEY_PASSPHRASE', $clientKeyPassphrase, 'Process')
    [Environment]::SetEnvironmentVariable(
        'STORAGEHUB_SFTP_ALTERNATE_KEY_PASSPHRASE', $alternateKeyPassphrase, 'Process')
    $env:PYTHONPATH = $dependencyRoot
    & $pythonPath -S (Join-Path $repositoryRoot 'eng\fixtures\generate_sftp_keys.py') `
        --output $keyRoot
    if ($LASTEXITCODE -ne 0) {
        throw 'The SFTP fixture keys could not be generated.'
    }

    $ports = Get-LoopbackPorts 3
    $passwordPort = $ports[0]
    $privateKeyPort = $ports[1]
    $rotatedPort = $ports[2]
    $serverDefinitions = @(
        @{ Mode = 'password'; Port = $passwordPort; HostKey = Join-Path $keyRoot 'host.key' },
        @{ Mode = 'public-key'; Port = $privateKeyPort; HostKey = Join-Path $keyRoot 'host.key' },
        @{ Mode = 'password'; Port = $rotatedPort; HostKey = Join-Path $keyRoot 'rotated-host.key' }
    )
    $readyFiles = @()
    foreach ($definition in $serverDefinitions) {
        $identity = "server-$($definition.Port)"
        $serverRoot = Join-Path $runRoot $identity
        $readyFile = Join-Path $runRoot "$identity.ready"
        [IO.Directory]::CreateDirectory((Join-Path $serverRoot 'mounted')) | Out-Null
        $readyFiles += $readyFile
        Start-SftpFixtureProcess `
            -mode $definition.Mode `
            -port $definition.Port `
            -root $serverRoot `
            -readyFile $readyFile `
            -hostKey $definition.HostKey
    }

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $exited = @($fixtureProcesses | Where-Object { $_.HasExited })
        if ($exited.Count -gt 0) {
            throw 'An SFTP fixture process exited before all endpoints became ready.'
        }
        if (@($readyFiles | Where-Object { -not (Test-Path -LiteralPath $_ -PathType Leaf) }).Count -eq 0) {
            break
        }
        Start-Sleep -Milliseconds 200
    }
    if (@($readyFiles | Where-Object { -not (Test-Path -LiteralPath $_ -PathType Leaf) }).Count -ne 0) {
        throw 'The SFTP fixture endpoints did not become ready within 30 seconds.'
    }

    $hostFingerprint = (Get-Content -LiteralPath (Join-Path $keyRoot 'host.sha256') -Raw).Trim()
    $rotatedHostFingerprint =
        (Get-Content -LiteralPath (Join-Path $keyRoot 'rotated-host.sha256') -Raw).Trim()
    [Environment]::SetEnvironmentVariable('STORAGEHUB_SFTP_USERNAME', $username, 'Process')
    [Environment]::SetEnvironmentVariable('STORAGEHUB_SFTP_PASSWORD', $password, 'Process')
    [Environment]::SetEnvironmentVariable('STORAGEHUB_SFTP_PASSWORD_PORT', "$passwordPort", 'Process')
    [Environment]::SetEnvironmentVariable('STORAGEHUB_SFTP_PRIVATE_KEY_PORT', "$privateKeyPort", 'Process')
    [Environment]::SetEnvironmentVariable('STORAGEHUB_SFTP_ROTATED_PORT', "$rotatedPort", 'Process')
    [Environment]::SetEnvironmentVariable('STORAGEHUB_SFTP_HOST_SHA256', $hostFingerprint, 'Process')
    [Environment]::SetEnvironmentVariable(
        'STORAGEHUB_SFTP_ROTATED_HOST_SHA256', $rotatedHostFingerprint, 'Process')
    [Environment]::SetEnvironmentVariable(
        'STORAGEHUB_SFTP_CLIENT_KEY_PATH', (Join-Path $keyRoot 'client.key'), 'Process')
    [Environment]::SetEnvironmentVariable(
        'STORAGEHUB_SFTP_ALTERNATE_KEY_PATH', (Join-Path $keyRoot 'alternate-client.key'), 'Process')
    [Environment]::SetEnvironmentVariable('STORAGEHUB_REQUIRE_SFTP', '1', 'Process')

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
        'Category=SftpProviderIntegration',
        '--logger',
        'trx;LogFileName=sftp.trx'
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

    Write-Host 'Running password, encrypted-key, and changed-host-key SFTP conformance on loopback.'
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "The SFTP integration suite failed with exit code $LASTEXITCODE."
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

    Remove-VerifiedRunRoot
}
