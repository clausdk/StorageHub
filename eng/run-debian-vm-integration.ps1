[CmdletBinding()]
param(
    [string] $LabRoot = (Join-Path $env:LOCALAPPDATA 'StorageHub.VmLab\Debian13'),
    [switch] $Rebuild,
    [switch] $SkipTests,
    [switch] $KeepRunning
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$LabRoot = [IO.Path]::GetFullPath($LabRoot)
$imageName = 'debian-13-genericcloud-amd64.qcow2'
$debianBaseUri = 'https://cloud.debian.org/images/cloud/trixie/latest/'
$baseImage = Join-Path $LabRoot $imageName
$checksumFile = Join-Path $LabRoot 'SHA512SUMS'
$workingDisk = Join-Path $LabRoot 'storagehub-debian13.qcow2'
$vmdk = Join-Path $LabRoot 'storagehub-debian13.vmdk'
$vmx = Join-Path $LabRoot 'storagehub-debian13.vmx'
$statePath = Join-Path $LabRoot 'lab-state.json'
$seedRoot = Join-Path $LabRoot 'seed'
$keyRoot = Join-Path $LabRoot 'keys'
$serialBootstrap = Join-Path $LabRoot 'serial-bootstrap.log'
$serialVmware = Join-Path $LabRoot 'serial-vmware.log'
$knownHosts = Join-Path $LabRoot 'known_hosts'
$tunnelLog = Join-Path $LabRoot 'ssh-tunnel.log'
$vmrun = 'C:\Program Files (x86)\VMware\VMware Workstation\vmrun.exe'
$qemuImg = 'C:\Program Files\qemu\qemu-img.exe'
$qemuSystem = 'C:\Program Files\qemu\qemu-system-x86_64.exe'

function Assert-Tool([string] $path, [string] $description) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "$description was not found at '$path'."
    }
}

function Invoke-Checked([string] $fileName, [string[]] $arguments) {
    & $fileName @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "'$fileName' failed with exit code $LASTEXITCODE."
    }
}

function New-RandomHex([int] $bytes) {
    $buffer = [byte[]]::new($bytes)
    $random = [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $random.GetBytes($buffer)
    }
    finally {
        $random.Dispose()
    }
    return [BitConverter]::ToString($buffer).Replace('-', '').ToLowerInvariant()
}

function Get-FreeTcpPort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    try {
        $listener.Start()
        return ([Net.IPEndPoint] $listener.LocalEndpoint).Port
    }
    finally {
        $listener.Stop()
    }
}

function Wait-TcpPort([string] $hostName, [int] $port, [TimeSpan] $timeout) {
    $deadline = [DateTimeOffset]::UtcNow.Add($timeout)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $client = [Net.Sockets.TcpClient]::new()
        try {
            $pending = $client.ConnectAsync($hostName, $port)
            if ($pending.Wait(750) -and $client.Connected) {
                return
            }
        }
        catch {
        }
        finally {
            $client.Dispose()
        }
        Start-Sleep -Milliseconds 500
    }
    throw "Timed out waiting for $hostName`:$port."
}

function Start-ProcessWithArguments(
    [string] $fileName,
    [string[]] $arguments,
    [string] $stdout,
    [string] $stderr) {
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $fileName
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $false
    $startInfo.RedirectStandardError = $false
    $startInfo.Arguments = ($arguments | ForEach-Object {
        '"' + $_.Replace('"', '\"') + '"'
    }) -join ' '
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) {
        throw "Could not start '$fileName'."
    }
    return $process
}

function Complete-RedirectedProcess([Diagnostics.Process] $process) {
    # Output is inherited by the runner. The serial console remains persisted separately.
}

function Stop-LabVm {
    if (Test-Path -LiteralPath $vmx) {
        $running = & $vmrun -T ws list 2>$null
        if ($running -contains $vmx) {
            & $vmrun -T ws stop $vmx soft | Out-Null
        }
    }
}

Assert-Tool $vmrun 'VMware vmrun'
Assert-Tool $qemuImg 'QEMU disk converter'
Assert-Tool $qemuSystem 'QEMU bootstrap executable'
[IO.Directory]::CreateDirectory($LabRoot) | Out-Null
[IO.Directory]::CreateDirectory($seedRoot) | Out-Null
[IO.Directory]::CreateDirectory($keyRoot) | Out-Null

if (-not (Test-Path -LiteralPath $checksumFile -PathType Leaf)) {
    Invoke-WebRequest -Uri ($debianBaseUri + 'SHA512SUMS') -OutFile $checksumFile -UseBasicParsing
}
if (-not (Test-Path -LiteralPath $baseImage -PathType Leaf)) {
    Write-Host "Downloading official Debian 13 cloud image to $baseImage"
    Invoke-Checked 'curl.exe' @(
        '--fail', '--location', '--continue-at', '-', '--output', $baseImage,
        ($debianBaseUri + $imageName))
}
$checksumLine = Get-Content -LiteralPath $checksumFile |
    Where-Object { $_ -match ([regex]::Escape($imageName) + '$') } |
    Select-Object -First 1
if (-not $checksumLine) {
    throw "No SHA-512 entry was found for $imageName."
}
$expectedChecksum = ($checksumLine -split '\s+')[0].ToUpperInvariant()
$actualChecksum = (Get-FileHash -LiteralPath $baseImage -Algorithm SHA512).Hash
if ($actualChecksum -ne $expectedChecksum) {
    throw 'The Debian cloud image failed SHA-512 verification.'
}
Write-Host 'Official Debian image SHA-512 verified.'

if ($Rebuild) {
    Stop-LabVm
    foreach ($path in @($workingDisk, $vmdk, $vmx, $statePath, $serialBootstrap, $serialVmware, $knownHosts)) {
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Force
        }
    }
    foreach ($path in @($seedRoot, $keyRoot)) {
        if (Test-Path -LiteralPath $path) {
            $resolved = [IO.Path]::GetFullPath($path)
            if (-not $resolved.StartsWith($LabRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Refusing to clear unexpected lab path '$resolved'."
            }
            Get-ChildItem -LiteralPath $resolved -Force | Remove-Item -Recurse -Force
        }
    }
}

if (Test-Path -LiteralPath $statePath -PathType Leaf) {
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
}
else {
    $state = [ordered]@{
        FtpUsername = 'storagehub'
        FtpPassword = New-RandomHex 20
        SftpClientKeyPassphrase = New-RandomHex 20
        SftpAlternateKeyPassphrase = New-RandomHex 20
        FtpClientPfxPassword = New-RandomHex 20
        MinioAccessKey = 'storagehublab'
        MinioSecretKey = New-RandomHex 24
        MinioBucket = 'storagehub-vm-lab'
    }
    [IO.File]::WriteAllText($statePath, ($state | ConvertTo-Json))
}

$adminKey = Join-Path $keyRoot 'admin_ed25519'
$clientKey = Join-Path $keyRoot 'client_ed25519.key'
$alternateKey = Join-Path $keyRoot 'alternate_ed25519.key'
if (-not (Test-Path -LiteralPath $adminKey)) {
    Invoke-Checked 'ssh-keygen.exe' @('-q', '-t', 'ed25519', '-N', '""', '-C', 'storagehub-vm-lab-admin', '-f', $adminKey)
}
if (-not (Test-Path -LiteralPath $clientKey)) {
    Invoke-Checked 'ssh-keygen.exe' @('-q', '-t', 'ed25519', '-N', $state.SftpClientKeyPassphrase, '-C', 'storagehub-vm-lab-client', '-f', $clientKey)
}
if (-not (Test-Path -LiteralPath $alternateKey)) {
    Invoke-Checked 'ssh-keygen.exe' @('-q', '-t', 'ed25519', '-N', $state.SftpAlternateKeyPassphrase, '-C', 'storagehub-vm-lab-alternate', '-f', $alternateKey)
}

if (-not (Test-Path -LiteralPath $vmdk -PathType Leaf)) {
    $adminPublicKey = (Get-Content -LiteralPath ($adminKey + '.pub') -Raw).Trim()
    $clientPublicKey = (Get-Content -LiteralPath ($clientKey + '.pub') -Raw).Trim()
    $metaData = @"
instance-id: storagehub-debian13-v1
local-hostname: storagehub-debian13
network:
  version: 2
  ethernets:
    all:
      match:
        name: "e*"
      dhcp4: true
"@
    $userData = @"
#cloud-config
users:
  - default
  - name: labadmin
    groups: [sudo]
    shell: /bin/bash
    lock_passwd: true
    sudo: "ALL=(ALL) NOPASSWD:ALL"
    ssh_authorized_keys:
      - $adminPublicKey
package_update: true
packages:
  - open-vm-tools
  - vsftpd
write_files:
  - path: /usr/local/sbin/storagehub-lab-setup
    permissions: '0700'
    content: |
      #!/bin/bash
      set -euo pipefail
      useradd --create-home --shell /bin/bash storagehub || true
      echo 'storagehub:$($state.FtpPassword)' | chpasswd
      install -d -m 700 -o storagehub -g storagehub /home/storagehub/.ssh
      printf '%s\n' '$clientPublicKey' > /home/storagehub/.ssh/authorized_keys
      chown storagehub:storagehub /home/storagehub/.ssh/authorized_keys
      chmod 600 /home/storagehub/.ssh/authorized_keys
      install -d -m 755 -o storagehub -g storagehub /home/storagehub/mounted
      ssh-keygen -q -t ed25519 -N '' -f /etc/ssh/storagehub_rotated_ed25519
      for mode in password publickey rotated; do
        port=2222
        password_auth=yes
        publickey_auth=no
        host_key=/etc/ssh/ssh_host_ed25519_key
        if [ "`$mode" = publickey ]; then port=2223; password_auth=no; publickey_auth=yes; fi
        if [ "`$mode" = rotated ]; then port=2224; host_key=/etc/ssh/storagehub_rotated_ed25519; fi
        cat > /etc/ssh/sshd_config_storagehub_`$mode <<EOF
Port `$port
ListenAddress 0.0.0.0
PidFile /run/sshd-storagehub-`$mode.pid
HostKey `$host_key
PasswordAuthentication `$password_auth
PubkeyAuthentication `$publickey_auth
KbdInteractiveAuthentication no
UsePAM yes
PermitRootLogin no
AllowUsers storagehub
AuthorizedKeysFile .ssh/authorized_keys
Subsystem sftp internal-sftp
EOF
        cat > /etc/systemd/system/storagehub-sshd-`$mode.service <<EOF
[Unit]
Description=StorageHub lab SSH (`$mode)
After=network.target ssh.service
[Service]
ExecStart=/usr/sbin/sshd -D -e -f /etc/ssh/sshd_config_storagehub_`$mode
Restart=on-failure
[Install]
WantedBy=multi-user.target
EOF
      done

      install -d -m 700 /etc/storagehub-lab
      openssl req -x509 -newkey rsa:2048 -nodes -days 3650 -subj '/CN=StorageHub Lab CA' -keyout /etc/storagehub-lab/ca.key -out /etc/storagehub-lab/ca.crt
      openssl req -newkey rsa:2048 -nodes -subj '/CN=storagehub-debian13' -keyout /etc/storagehub-lab/server.key -out /etc/storagehub-lab/server.csr
      openssl x509 -req -days 3650 -in /etc/storagehub-lab/server.csr -CA /etc/storagehub-lab/ca.crt -CAkey /etc/storagehub-lab/ca.key -CAcreateserial -out /etc/storagehub-lab/server.crt
      openssl x509 -in /etc/storagehub-lab/server.crt -outform DER -out /etc/storagehub-lab/server.der
      openssl req -newkey rsa:2048 -nodes -subj '/CN=StorageHub Lab Client' -keyout /etc/storagehub-lab/client.key -out /etc/storagehub-lab/client.csr
      openssl x509 -req -days 3650 -in /etc/storagehub-lab/client.csr -CA /etc/storagehub-lab/ca.crt -CAkey /etc/storagehub-lab/ca.key -CAcreateserial -out /etc/storagehub-lab/client.crt
      openssl pkcs12 -export -out /etc/storagehub-lab/client.pfx -inkey /etc/storagehub-lab/client.key -in /etc/storagehub-lab/client.crt -certfile /etc/storagehub-lab/ca.crt -passout pass:$($state.FtpClientPfxPassword)
      chmod 600 /etc/storagehub-lab/*

      LAB_IP=`$(hostname -I | awk '{print `$1}')
      make_vsftpd() {
        mode="`$1"; port="`$2"; min_port="`$3"; max_port="`$4"; tls="`$5"; implicit="`$6"; mtls="`$7"
        cat > /etc/vsftpd-storagehub-`$mode.conf <<EOF
listen=YES
listen_ipv6=NO
listen_port=`$port
anonymous_enable=NO
local_enable=YES
write_enable=YES
local_umask=022
chroot_local_user=YES
allow_writeable_chroot=YES
local_root=/home/storagehub
pam_service_name=vsftpd
pasv_enable=YES
pasv_address=`$LAB_IP
pasv_min_port=`$min_port
pasv_max_port=`$max_port
ssl_enable=`$tls
rsa_cert_file=/etc/storagehub-lab/server.crt
rsa_private_key_file=/etc/storagehub-lab/server.key
ca_certs_file=/etc/storagehub-lab/ca.crt
force_local_logins_ssl=`$tls
force_local_data_ssl=`$tls
ssl_tlsv1=YES
ssl_sslv2=NO
ssl_sslv3=NO
implicit_ssl=`$implicit
require_cert=`$mtls
validate_cert=`$mtls
EOF
        cat > /etc/systemd/system/storagehub-vsftpd-`$mode.service <<EOF
[Unit]
Description=StorageHub lab FTP (`$mode)
After=network.target
[Service]
ExecStart=/usr/sbin/vsftpd /etc/vsftpd-storagehub-`$mode.conf
Restart=on-failure
[Install]
WantedBy=multi-user.target
EOF
      }
      make_vsftpd plain 2121 30000 30009 NO NO NO
      make_vsftpd explicit 2122 30010 30019 YES NO NO
      make_vsftpd implicit 2990 30020 30029 YES YES NO
      make_vsftpd mtls 2124 30030 30039 YES NO YES

      curl --fail --location --retry 5 --output /usr/local/bin/minio https://dl.min.io/server/minio/release/linux-amd64/minio
      chmod 755 /usr/local/bin/minio
      useradd --system --home-dir /var/lib/storagehub-minio --shell /usr/sbin/nologin minio || true
      install -d -m 750 -o minio -g minio /var/lib/storagehub-minio/data
      cat > /etc/systemd/system/storagehub-minio.service <<EOF
[Unit]
Description=StorageHub lab MinIO
After=network-online.target
Wants=network-online.target
[Service]
User=minio
Group=minio
Environment=MINIO_ROOT_USER=$($state.MinioAccessKey)
Environment=MINIO_ROOT_PASSWORD=$($state.MinioSecretKey)
Environment=MINIO_BROWSER=off
ExecStart=/usr/local/bin/minio server --address :9000 /var/lib/storagehub-minio/data
Restart=on-failure
[Install]
WantedBy=multi-user.target
EOF
      systemctl daemon-reload
      systemctl enable storagehub-sshd-password storagehub-sshd-publickey storagehub-sshd-rotated
      systemctl enable storagehub-vsftpd-plain storagehub-vsftpd-explicit storagehub-vsftpd-implicit storagehub-vsftpd-mtls
      systemctl enable storagehub-minio
      systemctl start storagehub-sshd-password storagehub-sshd-publickey storagehub-sshd-rotated
      systemctl start storagehub-vsftpd-plain storagehub-vsftpd-explicit storagehub-vsftpd-implicit storagehub-vsftpd-mtls
      systemctl start storagehub-minio
      install -d -m 755 /var/lib/storagehub-lab
      date --iso-8601=seconds > /var/lib/storagehub-lab/ready
runcmd:
  - [bash, /usr/local/sbin/storagehub-lab-setup]
power_state:
  delay: now
  mode: poweroff
  message: StorageHub lab bootstrap complete
  timeout: 30
"@
    $setupTemplatePath = Join-Path $repositoryRoot 'eng\vm-lab\storagehub-lab-setup.sh'
    $setupScript = (Get-Content -LiteralPath $setupTemplatePath -Raw).
        Replace('__FTP_PASSWORD__', $state.FtpPassword).
        Replace('__CLIENT_PUBLIC_KEY__', $clientPublicKey).
        Replace('__PFX_PASSWORD__', $state.FtpClientPfxPassword).
        Replace('__MINIO_ACCESS_KEY__', $state.MinioAccessKey).
        Replace('__MINIO_SECRET_KEY__', $state.MinioSecretKey)
    $setupBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($setupScript))
    $userData = @"
#cloud-config
users:
  - default
  - name: labadmin
    groups: [sudo]
    shell: /bin/bash
    lock_passwd: true
    sudo: "ALL=(ALL) NOPASSWD:ALL"
    ssh_authorized_keys:
      - $adminPublicKey
package_update: true
packages:
  - open-vm-tools
  - vsftpd
write_files:
  - path: /usr/local/sbin/storagehub-lab-setup
    permissions: '0700'
    encoding: b64
    content: $setupBase64
runcmd:
  - [bash, /usr/local/sbin/storagehub-lab-setup]
power_state:
  delay: now
  mode: poweroff
  message: StorageHub lab bootstrap complete
  timeout: 30
"@
    [IO.File]::WriteAllText((Join-Path $seedRoot 'meta-data'), $metaData, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $seedRoot 'user-data'), $userData, [Text.UTF8Encoding]::new($false))

    $bootstrapComplete = (Test-Path -LiteralPath $workingDisk -PathType Leaf) -and
        (Test-Path -LiteralPath $serialBootstrap -PathType Leaf) -and
        (Select-String -LiteralPath $serialBootstrap -SimpleMatch 'reboot: Power down' -Quiet)
    if (-not $bootstrapComplete) {
        if (Test-Path -LiteralPath $workingDisk) {
            Remove-Item -LiteralPath $workingDisk -Force
        }
        Invoke-Checked $qemuImg @('create', '-f', 'qcow2', '-F', 'qcow2', '-b', $baseImage, $workingDisk, '10G')
        $seedPort = Get-FreeTcpPort
        $httpOut = Join-Path $LabRoot 'seed-http.out.log'
        $httpErr = Join-Path $LabRoot 'seed-http.err.log'
        $http = Start-ProcessWithArguments 'python.exe' @(
            '-m', 'http.server', "$seedPort", '--bind', '127.0.0.1', '--directory', $seedRoot) $httpOut $httpErr
        try {
        Wait-TcpPort '127.0.0.1' $seedPort ([TimeSpan]::FromSeconds(10))
        Write-Host 'Bootstrapping Debian services headlessly; this can take several minutes.'
        $qemuOut = Join-Path $LabRoot 'qemu-bootstrap.out.log'
        $qemuErr = Join-Path $LabRoot 'qemu-bootstrap.err.log'
        $qemuArguments = @(
            '-machine', 'q35', '-accel', 'tcg,thread=multi', '-cpu', 'max', '-m', '2048', '-smp', '2',
            '-drive', "file=$workingDisk,if=virtio,format=qcow2",
            '-netdev', 'user,id=net0', '-device', 'virtio-net-pci,netdev=net0',
            '-smbios', "type=1,serial=ds=nocloud-net;s=http://10.0.2.2:$seedPort/",
            '-display', 'none', '-monitor', 'none', '-serial', "file:$serialBootstrap", '-no-reboot')
        $qemu = Start-ProcessWithArguments $qemuSystem $qemuArguments $qemuOut $qemuErr
        if (-not $qemu.WaitForExit(1200000)) {
            $qemu.Kill($true)
            throw 'Debian bootstrap did not power off within 20 minutes.'
        }
        Complete-RedirectedProcess $qemu
        if ($qemu.ExitCode -ne 0) {
            throw "QEMU bootstrap failed with exit code $($qemu.ExitCode). See $qemuErr and $serialBootstrap."
        }
        }
        finally {
            if (-not $http.HasExited) {
                $http.Kill($true)
            }
            $http.WaitForExit()
            Complete-RedirectedProcess $http
            $http.Dispose()
        }
    }

    Write-Host 'Converting the bootstrapped disk to VMware VMDK.'
    Invoke-Checked $qemuImg @(
        'convert', '-p', '-f', 'qcow2', '-O', 'vmdk',
        '-o', 'subformat=monolithicSparse,adapter_type=lsilogic', $workingDisk, $vmdk)
}

$vmwareMetadata = @"
instance-id: storagehub-debian13-vmware-v1
local-hostname: storagehub-debian13
wait-on-network:
  ipv4: true
network:
  version: 2
  ethernets:
    all:
      match:
        name: "e*"
      dhcp4: true
redact:
  - userdata
"@
$vmwareRefreshScript = @'
#!/bin/bash
set -euo pipefail
LAB_IP=$(hostname -I | awk '{print $1}')
sed -i "s/^pasv_address=.*/pasv_address=$LAB_IP/" /etc/vsftpd-storagehub-*.conf
systemctl restart storagehub-vsftpd-plain storagehub-vsftpd-explicit storagehub-vsftpd-implicit storagehub-vsftpd-mtls
'@
$vmwareRefreshBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($vmwareRefreshScript))
$vmwareUserData = @"
#cloud-config
write_files:
  - path: /usr/local/sbin/storagehub-vmware-network-ready
    permissions: '0700'
    encoding: b64
    content: $vmwareRefreshBase64
runcmd:
  - [bash, /usr/local/sbin/storagehub-vmware-network-ready]
"@
$vmwareMetadataBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($vmwareMetadata))
$vmwareUserDataBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($vmwareUserData))
$vmxText = @"
.encoding = "UTF-8"
config.version = "8"
virtualHW.version = "21"
displayName = "StorageHub Debian 13 Lab"
guestOS = "debian12-64"
firmware = "efi"
memsize = "2048"
numvcpus = "2"
pciBridge0.present = "TRUE"
pciBridge4.present = "TRUE"
pciBridge4.virtualDev = "pcieRootPort"
pciBridge4.functions = "8"
pciBridge5.present = "TRUE"
pciBridge5.virtualDev = "pcieRootPort"
pciBridge5.functions = "8"
pciBridge6.present = "TRUE"
pciBridge6.virtualDev = "pcieRootPort"
pciBridge6.functions = "8"
pciBridge7.present = "TRUE"
pciBridge7.virtualDev = "pcieRootPort"
pciBridge7.functions = "8"
ide0:0.present = "TRUE"
ide0:0.fileName = "$([IO.Path]::GetFileName($vmdk))"
ethernet0.present = "TRUE"
ethernet0.connectionType = "nat"
ethernet0.virtualDev = "vmxnet3"
ethernet0.addressType = "generated"
serial0.present = "TRUE"
serial0.fileType = "file"
serial0.fileName = "$([IO.Path]::GetFileName($serialVmware))"
serial0.tryNoRxLoss = "FALSE"
tools.syncTime = "TRUE"
msg.autoAnswer = "TRUE"
uuid.action = "create"
guestinfo.metadata = "$vmwareMetadataBase64"
guestinfo.metadata.encoding = "base64"
guestinfo.userdata = "$vmwareUserDataBase64"
guestinfo.userdata.encoding = "base64"
"@
[IO.File]::WriteAllText($vmx, $vmxText, [Text.UTF8Encoding]::new($false))

$running = & $vmrun -T ws list
if ($running -notcontains $vmx) {
    Write-Host 'Starting Debian 13 in VMware Workstation (headless, NAT-only).'
    Invoke-Checked $vmrun @('-T', 'ws', 'start', $vmx, 'nogui')
}
$vmIp = (& $vmrun -T ws getGuestIPAddress $vmx -wait).Trim()
$parsedVmIp = $null
if ($LASTEXITCODE -ne 0 -or -not [Net.IPAddress]::TryParse($vmIp, [ref] $parsedVmIp)) {
    throw "VMware did not report a valid guest IP address: '$vmIp'."
}
Write-Host "VMware guest address: $vmIp"
Wait-TcpPort $vmIp 22 ([TimeSpan]::FromMinutes(3))

$sshCommon = @(
    '-i', $adminKey, '-o', 'BatchMode=yes', '-o', 'IdentitiesOnly=yes',
    '-o', "UserKnownHostsFile=$knownHosts", '-o', 'StrictHostKeyChecking=accept-new',
    '-o', 'ConnectTimeout=10')
Invoke-Checked 'ssh.exe' @($sshCommon + @(
    "labadmin@$vmIp",
    'sudo test -f /var/lib/storagehub-lab/ready && ' +
    'sudo install -d -m 755 -o storagehub -g storagehub /mounted && ' +
    'sudo systemctl restart storagehub-sshd-password storagehub-sshd-publickey storagehub-sshd-rotated'))

$serverDer = Join-Path $keyRoot 'server.der'
$clientPfx = Join-Path $keyRoot 'client.pfx'
$hostPublic = Join-Path $keyRoot 'ssh_host_ed25519_key.pub'
$rotatedPublic = Join-Path $keyRoot 'storagehub_rotated_ed25519.pub'
Invoke-Checked 'ssh.exe' @($sshCommon + @(
    "labadmin@$vmIp",
    'sudo install -d -m 700 -o labadmin -g labadmin /home/labadmin/storagehub-export && ' +
    'sudo cp /etc/storagehub-lab/server.der /etc/storagehub-lab/client.pfx ' +
    '/etc/ssh/ssh_host_ed25519_key.pub /etc/ssh/storagehub_rotated_ed25519.pub ' +
    '/home/labadmin/storagehub-export/ && ' +
    'sudo chown labadmin:labadmin /home/labadmin/storagehub-export/*'))
foreach ($copy in @(
    @{ Remote = '/home/labadmin/storagehub-export/server.der'; Local = $serverDer },
    @{ Remote = '/home/labadmin/storagehub-export/client.pfx'; Local = $clientPfx },
    @{ Remote = '/home/labadmin/storagehub-export/ssh_host_ed25519_key.pub'; Local = $hostPublic },
    @{ Remote = '/home/labadmin/storagehub-export/storagehub_rotated_ed25519.pub'; Local = $rotatedPublic })) {
    Invoke-Checked 'scp.exe' @($sshCommon + @("labadmin@$vmIp`:$($copy.Remote)", $copy.Local))
}

function Get-SshFingerprintHex([string] $publicKeyPath) {
    $parts = (Get-Content -LiteralPath $publicKeyPath -Raw).Trim() -split '\s+'
    if ($parts.Count -lt 2) { throw "Invalid SSH public key '$publicKeyPath'." }
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $digest = $sha256.ComputeHash([Convert]::FromBase64String($parts[1]))
        return [BitConverter]::ToString($digest).Replace('-', '')
    }
    finally {
        $sha256.Dispose()
    }
}

$localPorts = [ordered]@{
    SftpPassword = Get-FreeTcpPort
    SftpPrivateKey = Get-FreeTcpPort
    SftpRotated = Get-FreeTcpPort
    FtpPlain = Get-FreeTcpPort
    FtpExplicit = Get-FreeTcpPort
    FtpImplicit = Get-FreeTcpPort
    FtpMutualTls = Get-FreeTcpPort
    Minio = Get-FreeTcpPort
}
$forwardArguments = @('-N') + $sshCommon
foreach ($mapping in @(
    @($localPorts.SftpPassword, 2222), @($localPorts.SftpPrivateKey, 2223), @($localPorts.SftpRotated, 2224),
    @($localPorts.FtpPlain, 2121), @($localPorts.FtpExplicit, 2122), @($localPorts.FtpImplicit, 2990),
    @($localPorts.FtpMutualTls, 2124), @($localPorts.Minio, 9000))) {
    $forwardArguments += @('-L', "$($mapping[0]):127.0.0.1:$($mapping[1])")
}
foreach ($passivePort in 30000..30039) {
    $forwardArguments += @('-L', "$passivePort`:127.0.0.1:$passivePort")
}
$forwardArguments += @('-o', 'ExitOnForwardFailure=yes', "labadmin@$vmIp")
$tunnel = Start-ProcessWithArguments 'ssh.exe' $forwardArguments $tunnelLog ($tunnelLog + '.err')
try {
    foreach ($port in $localPorts.Values) {
        Wait-TcpPort '127.0.0.1' $port ([TimeSpan]::FromSeconds(20))
    }

    $environment = [ordered]@{
        STORAGEHUB_SFTP_USERNAME = $state.FtpUsername
        STORAGEHUB_SFTP_PASSWORD = $state.FtpPassword
        STORAGEHUB_SFTP_CLIENT_KEY_PASSPHRASE = $state.SftpClientKeyPassphrase
        STORAGEHUB_SFTP_ALTERNATE_KEY_PASSPHRASE = $state.SftpAlternateKeyPassphrase
        STORAGEHUB_SFTP_PASSWORD_PORT = "$($localPorts.SftpPassword)"
        STORAGEHUB_SFTP_PRIVATE_KEY_PORT = "$($localPorts.SftpPrivateKey)"
        STORAGEHUB_SFTP_ROTATED_PORT = "$($localPorts.SftpRotated)"
        STORAGEHUB_SFTP_HOST_SHA256 = Get-SshFingerprintHex $hostPublic
        STORAGEHUB_SFTP_ROTATED_HOST_SHA256 = Get-SshFingerprintHex $rotatedPublic
        STORAGEHUB_SFTP_CLIENT_KEY_PATH = $clientKey
        STORAGEHUB_SFTP_ALTERNATE_KEY_PATH = $alternateKey
        STORAGEHUB_REQUIRE_SFTP = '1'
        STORAGEHUB_FTP_USERNAME = $state.FtpUsername
        STORAGEHUB_FTP_PASSWORD = $state.FtpPassword
        STORAGEHUB_FTP_PLAIN_PORT = "$($localPorts.FtpPlain)"
        STORAGEHUB_FTP_EXPLICIT_PORT = "$($localPorts.FtpExplicit)"
        STORAGEHUB_FTP_IMPLICIT_PORT = "$($localPorts.FtpImplicit)"
        STORAGEHUB_FTP_MTLS_PORT = "$($localPorts.FtpMutualTls)"
        STORAGEHUB_FTP_SERVER_SHA256 = (Get-FileHash -LiteralPath $serverDer -Algorithm SHA256).Hash
        STORAGEHUB_FTP_CLIENT_PFX_PATH = $clientPfx
        STORAGEHUB_FTP_CLIENT_PFX_PASSWORD = $state.FtpClientPfxPassword
        STORAGEHUB_REQUIRE_FTP = '1'
        STORAGEHUB_MINIO_ENDPOINT = "http://127.0.0.1:$($localPorts.Minio)/"
        STORAGEHUB_MINIO_ACCESS_KEY = $state.MinioAccessKey
        STORAGEHUB_MINIO_SECRET_KEY = $state.MinioSecretKey
        STORAGEHUB_MINIO_BUCKET = $state.MinioBucket
        STORAGEHUB_REQUIRE_MINIO = '1'
        STORAGEHUB_REQUIRE_VM_CROSS_PROVIDER = '1'
    }
    $originalEnvironment = @{}
    foreach ($entry in $environment.GetEnumerator()) {
        $originalEnvironment[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key, 'Process')
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
    }
    try {
        if (-not $SkipTests) {
            Write-Host 'Running StorageHub S3, FTP/FTPS, SFTP, SSH terminal, and cross-provider VM tests.'
            Invoke-Checked 'dotnet.exe' @(
                'build', (Join-Path $repositoryRoot 'tests\StorageHub.Storage.CodeLogic.Tests\StorageHub.Storage.CodeLogic.Tests.csproj'),
                '--configuration', 'Release', '--no-restore')
            Invoke-Checked 'dotnet.exe' @(
                'build', (Join-Path $repositoryRoot 'tests\StorageHub.Agent.Windows.Tests\StorageHub.Agent.Windows.Tests.csproj'),
                '--configuration', 'Release', '--no-restore')
            Invoke-Checked 'ssh.exe' @($sshCommon + @(
                "labadmin@$vmIp",
                'sudo find /home/storagehub/mounted -mindepth 1 -maxdepth 1 -exec rm -rf -- {} + && ' +
                'sudo find /mounted -mindepth 1 -maxdepth 1 -exec rm -rf -- {} +'))
            foreach ($category in @(
                'ProviderIntegration',
                'FtpProviderIntegration',
                'SftpProviderIntegration',
                'VmCrossProviderIntegration')) {
                Invoke-Checked 'dotnet.exe' @(
                    'test', (Join-Path $repositoryRoot 'tests\StorageHub.Storage.CodeLogic.Tests\StorageHub.Storage.CodeLogic.Tests.csproj'),
                    '--configuration', 'Release', '--no-build', '--no-restore', '--filter', "Category=$category")
            }
            Invoke-Checked 'dotnet.exe' @(
                'test', (Join-Path $repositoryRoot 'tests\StorageHub.Agent.Windows.Tests\StorageHub.Agent.Windows.Tests.csproj'),
                '--configuration', 'Release', '--no-build', '--no-restore',
                '--filter', 'Category=SftpHostKeyDiscoveryIntegration|Category=SshTerminalIntegration')
        }
        $connectionInfo = [ordered]@{
            VmwareVmx = $vmx
            GuestIp = $vmIp
            Username = $state.FtpUsername
            SshPasswordPort = $localPorts.SftpPassword
            SshPrivateKeyPort = $localPorts.SftpPrivateKey
            FtpPort = $localPorts.FtpPlain
            ExplicitFtpsPort = $localPorts.FtpExplicit
            ImplicitFtpsPort = $localPorts.FtpImplicit
            MutualTlsFtpsPort = $localPorts.FtpMutualTls
            S3Endpoint = "http://127.0.0.1:$($localPorts.Minio)/"
            S3Bucket = $state.MinioBucket
            TunnelPid = $tunnel.Id
        }
        [IO.File]::WriteAllText((Join-Path $LabRoot 'connection-info.json'), ($connectionInfo | ConvertTo-Json))
        Write-Host "Debian VM lab is healthy. Connection details: $(Join-Path $LabRoot 'connection-info.json')"
    }
    finally {
        foreach ($entry in $environment.GetEnumerator()) {
            [Environment]::SetEnvironmentVariable($entry.Key, $originalEnvironment[$entry.Key], 'Process')
        }
    }
}
finally {
    if (-not $KeepRunning) {
        if (-not $tunnel.HasExited) {
            $tunnel.Kill($true)
        }
        $tunnel.WaitForExit()
        Complete-RedirectedProcess $tunnel
        $tunnel.Dispose()
        Stop-LabVm
    }
    else {
        Write-Host "VMware lab and SSH tunnel remain running (tunnel PID $($tunnel.Id))."
    }
}
