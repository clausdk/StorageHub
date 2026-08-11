# Release engineering

StorageHub publishes an unsigned engineering prerelease for every successful
push to `main`. Pull requests build and smoke-test the same installer, but never
receive a write-capable GitHub token and never publish a release.

## Release contents

The Windows bundle is built for `win-x64` with the .NET runtime included. The
Desktop and Agent are published as complete directory trees; trimming, native
AOT, ReadyToRun, and single-file bundling remain disabled because StorageHub,
WinForms, provider SDKs, SQLite native assets, and `CL.Storage` use runtime
discovery and platform-specific files.

The release contains the per-user Velopack Setup executable and MSI, a portable
ZIP, Velopack update metadata/packages, separated symbols when available,
`release-version.txt`, and `SHA256SUMS`. The setup process is intentionally
unelevated. It must not install the Agent as a Windows service because StorageHub
secrets, instance ownership, and named pipes are scoped to the signed-in Windows
user.

Installed builds use Velopack's GitHub source against the fixed public repository
`https://github.com/clausdk/StorageHub`, retain their packaged channel, reject
downgrades, and can silently download and apply the exact package described by
the release feed. Portable and developer builds fail closed without checking or
modifying an installation. Automatic checks/downloads, preview inclusion, and
automatic restart are persisted per user; automatic restart is opt-in. Because
Velopack's framework-level apply-on-startup default is explicitly disabled, a
pending download cannot bypass those persisted StorageHub preferences. Because
preview packages are not yet Authenticode-signed, feed/package checksum
verification provides integrity but is not a substitute for the production
signing release gate.

Uninstall removes program files and autostart registration but deliberately
preserves `%LOCALAPPDATA%\StorageHub`. Deleting durable state, connection
profiles, trust decisions, schedules, or the encrypted vault requires a separate
explicit user action.

## Build a bundle locally

Run:

```powershell
& .\eng\package-windows.ps1 `
  -Version '0.1.0-local.1' `
  -OutputRoot artifacts\local-release
```

The packaging script restores the repository-pinned `vpk` tool and validates
every expected output. The install/uninstall smoke script refuses to run on a
normal workstation by default because it mutates the current user's installed
program and autostart state and could collide with a real StorageHub session.
Run it only on a disposable Windows runner, Sandbox, or test account.
Outside CI, both `-AllowOutsideCi` and
`-ConfirmDisposableRunner` are required before it will make system changes.

## Automated publication

The CI workflow uses a least-privilege job chain:

1. build, test, audit NuGet and hash-locked Python dependencies, and run the
   SHA-256-pinned MinIO/S3 plus FTP/FTPS and SFTP fixtures with read-only repository
   access;
2. package and silently install/test/uninstall on a disposable Windows runner;
3. attest the exact same-run artifacts after a successful `main` push; and
4. create or verify the prerelease for the exact commit without checking out or
   executing repository code in the write-capable publication job.

Push concurrency is keyed by commit SHA, so a newer push cannot cancel an older
successful push before its release is produced. Rerunning one workflow is
idempotent: it verifies the existing exact-commit tag and asset names, then
leaves the already-published prerelease immutable.

Production signing remains a release gate. Signing keys and PFX passwords must
be provided only by a protected signing system or GitHub environment; they must
never be stored in this repository, workflow artifacts, command output, or
ordinary repository secrets exposed to build steps.
