[CmdletBinding()]
param(
    [string] $LabRoot = (Join-Path $env:LOCALAPPDATA 'StorageHub\VmLab\Debian13')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$LabRoot = [IO.Path]::GetFullPath($LabRoot)
$connectionInfoPath = Join-Path $LabRoot 'connection-info.json'
$vmx = Join-Path $LabRoot 'storagehub-debian13.vmx'
$vmrun = 'C:\Program Files (x86)\VMware\VMware Workstation\vmrun.exe'

if (Test-Path -LiteralPath $connectionInfoPath -PathType Leaf) {
    $connectionInfo = Get-Content -LiteralPath $connectionInfoPath -Raw | ConvertFrom-Json
    if ($connectionInfo.TunnelPid -is [int] -or $connectionInfo.TunnelPid -is [long]) {
        $process = Get-CimInstance Win32_Process -Filter "ProcessId=$($connectionInfo.TunnelPid)" -ErrorAction SilentlyContinue
        if ($process -and
            $process.Name -eq 'ssh.exe' -and
            $process.CommandLine -match [regex]::Escape($LabRoot) -and
            $process.CommandLine -match '"-N"') {
            Stop-Process -Id $connectionInfo.TunnelPid -Force
            Write-Host "Stopped StorageHub lab tunnel PID $($connectionInfo.TunnelPid)."
        }
    }
}

if (Test-Path -LiteralPath $vmx -PathType Leaf) {
    $running = & $vmrun -T ws list
    if ($running -contains $vmx) {
        & $vmrun -T ws stop $vmx soft
        if ($LASTEXITCODE -ne 0) {
            throw "VMware could not stop the StorageHub lab VM at '$vmx'."
        }
        Write-Host 'Stopped StorageHub Debian 13 lab VM.'
    }
}
