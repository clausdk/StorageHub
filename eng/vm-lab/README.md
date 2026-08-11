# Debian 13 VMware integration lab

This lab runs StorageHub against a real Debian 13 guest in VMware Workstation. It provisions:

- OpenSSH on separate password, private-key, and rotated-host-key listeners
- SFTP through OpenSSH
- plain FTP, explicit FTPS, implicit FTPS, and mutual-TLS FTPS through vsftpd
- an S3-compatible MinIO service
- VMware Tools, DHCP, a serial console log, and NAT-only networking

The official Debian cloud image is downloaded and SHA-512 verified. A one-time headless QEMU boot applies cloud-init, after which the disk is converted to VMDK and every tested boot runs in VMware.

Run the complete lab and leave it available for manual UI testing:

```powershell
.\eng\run-debian-vm-integration.ps1 -KeepRunning
```

Connection ports and the VMware guest address are written to:

```text
%LOCALAPPDATA%\StorageHub\VmLab\Debian13\connection-info.json
```

Lab-only credentials, keys, certificates, disks, and logs remain under the same user-profile directory and are not stored in Git. The VM uses VMware NAT rather than bridged networking. The local SSH tunnel exposes provider ports only while it is running.

Stop the tunnel and VM without deleting the reusable disk:

```powershell
.\eng\stop-debian-vm-lab.ps1
```

Force a clean guest rebuild while retaining the verified Debian base download:

```powershell
.\eng\run-debian-vm-integration.ps1 -Rebuild -KeepRunning
```

The real-VM suite runs each provider fixture in a separate process because CodeLogic has a process-global runtime. It also verifies SFTP-to-S3 and FTPS-to-S3 streaming through `TransferExecutor`. New-file transfers in the reverse direction are expected to fail closed today because FTP/SFTP do not advertise native conditional-create support; the test proves that no partial destination is published.
