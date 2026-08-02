#Requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $BundleRoot,

    [switch] $AllowOutsideCi,

    [switch] $ConfirmDisposableRunner,

    [ValidateRange(30, 600)]
    [int] $ProcessTimeoutSeconds = 180
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Test-TrueEnvironmentValue {
    param(
        [Parameter(Mandatory)]
        [string] $Name
    )

    $value = [System.Environment]::GetEnvironmentVariable($Name)
    return $value -in @('1', 'true', 'yes')
}

function ConvertTo-WindowsCommandLineArgument {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string] $Value
    )

    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') {
        return $Value
    }

    $builder = [System.Text.StringBuilder]::new()
    [void] $builder.Append([char] 34)
    $backslashCount = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq [char] 92) {
            $backslashCount++
            continue
        }

        if ($character -eq [char] 34) {
            [void] $builder.Append([char] 92, ($backslashCount * 2) + 1)
            [void] $builder.Append([char] 34)
            $backslashCount = 0
            continue
        }

        if ($backslashCount -gt 0) {
            [void] $builder.Append([char] 92, $backslashCount)
            $backslashCount = 0
        }
        [void] $builder.Append($character)
    }

    if ($backslashCount -gt 0) {
        [void] $builder.Append([char] 92, $backslashCount * 2)
    }
    [void] $builder.Append([char] 34)
    return $builder.ToString()
}

function Invoke-CheckedProcess {
    param(
        [Parameter(Mandatory)]
        [string] $FilePath,

        [Parameter(Mandatory)]
        [string[]] $ArgumentList,

        [Parameter(Mandatory)]
        [hashtable] $EnvironmentVariables,

        [Parameter(Mandatory)]
        [string] $Description,

        [Parameter(Mandatory)]
        [int] $TimeoutSeconds
    )

    Write-Host "==> $Description"
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $quotedArguments = @(
        $ArgumentList | ForEach-Object { ConvertTo-WindowsCommandLineArgument -Value $_ }
    )
    $startInfo.Arguments = [string]::Join(' ', $quotedArguments)
    foreach ($name in $EnvironmentVariables.Keys) {
        $startInfo.EnvironmentVariables[$name] = [string] $EnvironmentVariables[$name]
    }

    $process = [System.Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) {
        throw "Could not start $Description."
    }

    try {
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            try {
                $process.Kill()
            }
            catch {
                Write-Warning "Could not terminate timed-out process $($process.Id): $($_.Exception.Message)"
            }
            throw "$Description did not exit within $TimeoutSeconds seconds."
        }

        if ($process.ExitCode -ne 0) {
            throw "$Description failed with exit code $($process.ExitCode)."
        }
    }
    finally {
        $process.Dispose()
    }
}

function Wait-ForCondition {
    param(
        [Parameter(Mandatory)]
        [scriptblock] $Condition,

        [Parameter(Mandatory)]
        [int] $TimeoutSeconds,

        [Parameter(Mandatory)]
        [string] $FailureMessage
    )

    $deadline = [System.DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if (& $Condition) {
            return
        }
        Start-Sleep -Milliseconds 250
    } while ([System.DateTimeOffset]::UtcNow -lt $deadline)

    throw $FailureMessage
}

function Assert-SeparateDirectoryTrees {
    param(
        [Parameter(Mandatory)]
        [string] $FirstPath,

        [Parameter(Mandatory)]
        [string] $SecondPath,

        [Parameter(Mandatory)]
        [string] $Description
    )

    $first = [System.IO.Path]::GetFullPath($FirstPath).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $second = [System.IO.Path]::GetFullPath($SecondPath).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $firstPrefix = $first + [System.IO.Path]::DirectorySeparatorChar
    $secondPrefix = $second + [System.IO.Path]::DirectorySeparatorChar
    if ($first.Equals($second, [System.StringComparison]::OrdinalIgnoreCase) -or
        $first.StartsWith($secondPrefix, [System.StringComparison]::OrdinalIgnoreCase) -or
        $second.StartsWith($firstPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description must be distinct, non-nested directory trees."
    }
}

function Get-StorageHubAutoStartEntries {
    $entries = [System.Collections.Generic.List[string]]::new()
    foreach ($registryPath in @(
            'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run',
            'HKCU:\Software\Microsoft\Windows\CurrentVersion\RunOnce')) {
        if (-not (Test-Path -LiteralPath $registryPath)) {
            continue
        }

        $properties = Get-ItemProperty -LiteralPath $registryPath
        foreach ($property in $properties.PSObject.Properties) {
            if ($property.Name.StartsWith('PS', [System.StringComparison]::Ordinal)) {
                continue
            }

            $entry = "$registryPath::$($property.Name)=$($property.Value)"
            if ($entry.IndexOf('StorageHub', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                $entries.Add($entry)
            }
        }
    }

    return @($entries | Sort-Object)
}

function Get-ProcessIdsByExecutablePath {
    param(
        [Parameter(Mandatory)]
        [string] $ExecutablePath
    )

    $expectedPath = [System.IO.Path]::GetFullPath($ExecutablePath)
    $processName = [System.IO.Path]::GetFileNameWithoutExtension($expectedPath)
    $processIds = [System.Collections.Generic.List[int]]::new()
    foreach ($process in Get-Process -Name $processName -ErrorAction SilentlyContinue) {
        try {
            if (-not $process.HasExited -and
                [string]::Equals(
                    [System.IO.Path]::GetFullPath($process.MainModule.FileName),
                    $expectedPath,
                    [System.StringComparison]::OrdinalIgnoreCase)) {
                $processIds.Add($process.Id)
            }
        }
        catch {
            # An inaccessible or exiting process is not the packaged Agent.
        }
        finally {
            $process.Dispose()
        }
    }

    return @($processIds)
}

function Test-ProcessHasExited {
    param(
        [Parameter(Mandatory)]
        [int] $ProcessId
    )

    try {
        $process = [System.Diagnostics.Process]::GetProcessById($ProcessId)
    }
    catch [System.ArgumentException] {
        return $true
    }

    try {
        return $process.HasExited
    }
    finally {
        $process.Dispose()
    }
}

if ([System.Environment]::OSVersion.Platform -ne [System.PlatformID]::Win32NT) {
    throw 'The Windows installer smoke test can only run on Windows.'
}

$isCi = (Test-TrueEnvironmentValue -Name 'CI') -or
    (Test-TrueEnvironmentValue -Name 'GITHUB_ACTIONS')
if (-not $isCi -and -not $AllowOutsideCi) {
    throw 'Installer execution is refused outside CI. Pass -AllowOutsideCi to acknowledge local system changes explicitly.'
}

$runnerEnvironment = [System.Environment]::GetEnvironmentVariable('RUNNER_ENVIRONMENT')
$isGithubHostedRunner = (Test-TrueEnvironmentValue -Name 'GITHUB_ACTIONS') -and
    [string]::Equals(
        $runnerEnvironment,
        'github-hosted',
        [System.StringComparison]::OrdinalIgnoreCase)
$isConfirmedDisposableRunner = $ConfirmDisposableRunner -or
    (Test-TrueEnvironmentValue -Name 'STORAGEHUB_DISPOSABLE_RUNNER') -or
    $isGithubHostedRunner
if (-not $isConfirmedDisposableRunner) {
    throw 'Installer execution is refused on a non-disposable worker. Use a GitHub-hosted runner or explicitly confirm an isolated test machine.'
}

$bundleFullPath = if ([System.IO.Path]::IsPathRooted($BundleRoot)) {
    [System.IO.Path]::GetFullPath($BundleRoot)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path (Get-Location).Path $BundleRoot))
}
if (-not (Test-Path -LiteralPath $bundleFullPath -PathType Container)) {
    throw "Release bundle '$bundleFullPath' does not exist."
}
$nestedBundleFiles = @(
    Get-ChildItem -LiteralPath $bundleFullPath -File -Recurse |
        Where-Object { $_.DirectoryName -cne $bundleFullPath }
)
if ($nestedBundleFiles.Count -ne 0) {
    throw "Release bundle '$bundleFullPath' is not flat."
}
$installerCandidates = @(
    Get-ChildItem -LiteralPath $bundleFullPath -Filter '*-Setup.exe' -File
)
if ($installerCandidates.Count -ne 1) {
    throw "Release bundle must contain exactly one *-Setup.exe; found $($installerCandidates.Count)."
}
$installerFullPath = $installerCandidates[0].FullName

$checksumsPath = Join-Path $bundleFullPath 'SHA256SUMS'
if (-not (Test-Path -LiteralPath $checksumsPath -PathType Leaf)) {
    throw 'Release bundle does not contain SHA256SUMS.'
}
$installerHashes = @(
    foreach ($line in Get-Content -LiteralPath $checksumsPath) {
        if ($line -match '^(?<hash>[0-9A-Fa-f]{64})\s+(?:\*)?(?<name>.+)$' -and
            $Matches.name.Trim() -ceq $installerCandidates[0].Name) {
            $Matches.hash
        }
    }
)
if ($installerHashes.Count -ne 1) {
    throw 'SHA256SUMS must contain exactly one entry for the Setup executable.'
}
$actualInstallerHash = (Get-FileHash -LiteralPath $installerFullPath -Algorithm SHA256).Hash
if ($actualInstallerHash -cne $installerHashes[0].ToUpperInvariant()) {
    throw 'The Setup executable does not match SHA256SUMS.'
}
$buildInfoPath = Join-Path $bundleFullPath 'BUILDINFO.json'
if (-not (Test-Path -LiteralPath $buildInfoPath -PathType Leaf)) {
    throw 'Release bundle does not contain BUILDINFO.json.'
}
try {
    $buildInfo = Get-Content -LiteralPath $buildInfoPath -Raw | ConvertFrom-Json
}
catch {
    throw 'Release bundle BUILDINFO.json is invalid.'
}
if ($buildInfo.packId -cne 'StorageHub.Desktop' -or $buildInfo.rid -cne 'win-x64') {
    throw 'Release bundle does not use the immutable StorageHub.Desktop win-x64 package identity.'
}

$smokeRoot = Join-Path `
    ([System.IO.Path]::GetTempPath()) `
    ("StorageHub-installer-smoke-" + [System.Guid]::NewGuid().ToString('N'))
$installDirectory = Join-Path $smokeRoot 'Install'
$dataRoot = Join-Path $smokeRoot 'Data'
$sentinelPath = Join-Path $dataRoot 'uninstall-preservation.sentinel'
$sentinelContent = [System.Guid]::NewGuid().ToString('D')

Assert-SeparateDirectoryTrees `
    -FirstPath $installDirectory `
    -SecondPath $dataRoot `
    -Description 'Installer smoke-test program and configured data directories'
$defaultDataRoot = Join-Path `
    ([System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::LocalApplicationData)) `
    'StorageHub'
Assert-SeparateDirectoryTrees `
    -FirstPath $installDirectory `
    -SecondPath $defaultDataRoot `
    -Description 'Installer program and default durable data directories'

$autoStartBefore = @(Get-StorageHubAutoStartEntries)

New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
[System.IO.File]::WriteAllText(
    $sentinelPath,
    $sentinelContent,
    [System.Text.UTF8Encoding]::new($false))

$childEnvironment = @{
    STORAGEHUB_AUTOSTART = '0'
    STORAGEHUB_DISABLE_AUTOSTART = '1'
    STORAGEHUB_DATA_ROOT = $dataRoot
}
$uninstallAttempted = $false
$completed = $false
$liveAgentProcessId = $null

try {
    # Velopack --silent suppresses its normal post-install first launch. The
    # environment switches also make future StorageHub autostart support opt out.
    Invoke-CheckedProcess `
        -FilePath $installerFullPath `
        -ArgumentList @('--silent', '--installto', $installDirectory) `
        -EnvironmentVariables $childEnvironment `
        -Description 'Silently install StorageHub' `
        -TimeoutSeconds $ProcessTimeoutSeconds

    $desktopExe = Join-Path $installDirectory 'current\StorageHub.Desktop.exe'
    $agentExe = Join-Path $installDirectory 'current\Agent\StorageHub.Agent.Windows.exe'
    $updateExe = Join-Path $installDirectory 'Update.exe'
    $stableDesktopExe = Join-Path $installDirectory 'StorageHub.Desktop.exe'
    Wait-ForCondition `
        -Condition {
            (Test-Path -LiteralPath $desktopExe -PathType Leaf) -and
            (Test-Path -LiteralPath $agentExe -PathType Leaf) -and
            (Test-Path -LiteralPath $updateExe -PathType Leaf) -and
            (Test-Path -LiteralPath $stableDesktopExe -PathType Leaf)
        } `
        -TimeoutSeconds 30 `
        -FailureMessage 'The installed Desktop, Agent, or Update executable was not found.'

    foreach ($requiredPayload in @(
            (Join-Path $installDirectory 'current\coreclr.dll'),
            (Join-Path $installDirectory 'current\Agent\coreclr.dll'),
            (Join-Path $installDirectory 'current\BUILDINFO.json'),
            (Join-Path $installDirectory 'current\release-version.txt'),
            (Join-Path $installDirectory 'current\LICENSE'),
            (Join-Path $installDirectory 'current\README.md'))) {
        if (-not (Test-Path -LiteralPath $requiredPayload -PathType Leaf)) {
            throw "Installed payload is missing '$requiredPayload'."
        }
    }
    if (@(Get-ChildItem -LiteralPath (Join-Path $installDirectory 'current') -Filter '*.pdb' -File -Recurse).Count -ne 0) {
        throw 'The installed payload contains program database symbols.'
    }

    $autoStartAfterInstall = @(Get-StorageHubAutoStartEntries)
    if ([string]::Join("`n", $autoStartAfterInstall) -ne
        [string]::Join("`n", $autoStartBefore)) {
        throw 'The installer added or changed a StorageHub Run/RunOnce autostart entry.'
    }

    Invoke-CheckedProcess `
        -FilePath $agentExe `
        -ArgumentList @('--run-once') `
        -EnvironmentVariables $childEnvironment `
        -Description 'Run the installed Agent once' `
        -TimeoutSeconds $ProcessTimeoutSeconds
    Invoke-CheckedProcess `
        -FilePath $agentExe `
        -ArgumentList @('--health') `
        -EnvironmentVariables $childEnvironment `
        -Description 'Run the installed Agent health check' `
        -TimeoutSeconds $ProcessTimeoutSeconds

    $lifecycleEnvironment = @{
        STORAGEHUB_AUTOSTART = '0'
        STORAGEHUB_DATA_ROOT = $dataRoot
    }
    Invoke-CheckedProcess `
        -FilePath $stableDesktopExe `
        -ArgumentList @('--agent-only') `
        -EnvironmentVariables $lifecycleEnvironment `
        -Description 'Start the packaged Agent through the stable Desktop launcher' `
        -TimeoutSeconds $ProcessTimeoutSeconds
    Wait-ForCondition `
        -Condition {
            @(Get-ProcessIdsByExecutablePath -ExecutablePath $agentExe).Count -eq 1
        } `
        -TimeoutSeconds 20 `
        -FailureMessage 'The stable Desktop launcher did not leave one packaged Agent running.'
    $liveAgentProcessIds = @(Get-ProcessIdsByExecutablePath -ExecutablePath $agentExe)
    if ($liveAgentProcessIds.Count -ne 1) {
        throw "Expected one live packaged Agent; found $($liveAgentProcessIds.Count)."
    }
    $liveAgentProcessId = $liveAgentProcessIds[0]

    $uninstallAttempted = $true
    Invoke-CheckedProcess `
        -FilePath $updateExe `
        -ArgumentList @('--silent', 'uninstall') `
        -EnvironmentVariables $childEnvironment `
        -Description 'Silently uninstall StorageHub' `
        -TimeoutSeconds $ProcessTimeoutSeconds

    Wait-ForCondition `
        -Condition {
            -not (Test-Path -LiteralPath $desktopExe) -and
            -not (Test-Path -LiteralPath $agentExe) -and
            -not (Test-Path -LiteralPath $stableDesktopExe) -and
            -not (Test-Path -LiteralPath $updateExe) -and
            (Test-ProcessHasExited -ProcessId $liveAgentProcessId)
        } `
        -TimeoutSeconds 45 `
        -FailureMessage 'StorageHub binaries or the live packaged Agent remain after silent uninstall.'

    if (-not (Test-Path -LiteralPath $sentinelPath -PathType Leaf)) {
        throw 'The uninstall removed the isolated StorageHub data directory.'
    }
    if ((Get-Content -LiteralPath $sentinelPath -Raw) -ne $sentinelContent) {
        throw 'The uninstall changed the isolated StorageHub data sentinel.'
    }

    $autoStartAfterUninstall = @(Get-StorageHubAutoStartEntries)
    if ([string]::Join("`n", $autoStartAfterUninstall) -ne
        [string]::Join("`n", $autoStartBefore)) {
        throw 'StorageHub autostart registry state was not restored after uninstall.'
    }

    $completed = $true
    Write-Host 'Installer smoke test passed: payload and lifecycle verified, live-Agent uninstall completed, and data was preserved.'
}
finally {
    if (-not $uninstallAttempted) {
        $fallbackUpdateExe = Join-Path $installDirectory 'Update.exe'
        if (Test-Path -LiteralPath $fallbackUpdateExe -PathType Leaf) {
            try {
                Invoke-CheckedProcess `
                    -FilePath $fallbackUpdateExe `
                    -ArgumentList @('--silent', 'uninstall') `
                    -EnvironmentVariables $childEnvironment `
                    -Description 'Best-effort cleanup uninstall' `
                    -TimeoutSeconds $ProcessTimeoutSeconds
            }
            catch {
                Write-Warning "Cleanup uninstall failed: $($_.Exception.Message)"
            }
        }
    }

    if ($completed) {
        if (Test-Path -LiteralPath $smokeRoot) {
            $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd(
                [System.IO.Path]::DirectorySeparatorChar,
                [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
            $validatedSmokeRoot = [System.IO.Path]::GetFullPath($smokeRoot)
            if (-not $validatedSmokeRoot.StartsWith(
                    $tempRoot,
                    [System.StringComparison]::OrdinalIgnoreCase) -or
                -not (Split-Path -Leaf $validatedSmokeRoot).StartsWith(
                    'StorageHub-installer-smoke-',
                    [System.StringComparison]::Ordinal)) {
                throw "Refusing to clean unexpected smoke-test path '$validatedSmokeRoot'."
            }
            Remove-Item -LiteralPath $validatedSmokeRoot -Recurse -Force
        }
    }
    elseif (Test-Path -LiteralPath $smokeRoot) {
        Write-Warning "Installer smoke diagnostics remain at '$smokeRoot'."
    }
}
