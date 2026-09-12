<p align="center">
  <img src="assets/branding/storagehub-icon.png" width="144" alt="StorageHub icon">
</p>

# StorageHub

StorageHub is an open-source, security-first file manager, transfer client, and
synchronization engine for Windows. It is built with C# and .NET 10, uses the
CodeLogic application lifecycle, and adapts the all-in-one `CL.Storage`
(`CodeLogic.Storage`) provider library behind a provider-neutral contract.

> [!IMPORTANT]
> StorageHub 1.0 is the first stable release. Release binaries are not yet
> Authenticode-signed, so Windows SmartScreen may warn on first run; verify
> downloads against the published `SHA256SUMS`. As with any file-management
> tool, keep an independent backup of irreplaceable data.

## Features

**Browse and manage** — Dual-pane workspaces of one to four equal-capability
panes, with asynchronous local and remote browsing, history, filtering, and
bounded paging. Create, rename, batch-rename, and delete items; inspect
versions, metadata, and tags read-only. Explorer drag and drop both ways.

**Connect securely** — Typed profiles for Local/UNC, S3, FTP, FTPS, and SFTP
storage plus SSH clients, organized in a grouped, searchable tree with folders,
favorites, and tags. Credentials live in a Windows DPAPI current-user vault and
never enter profile JSON, logs, or diagnostics. Server identity is pinned
explicitly: no certificate or host key is accepted on first contact.

**Transfer durably** — Any-to-any copy and move with source preconditions,
optional SHA-256 verification, and delete-only-after-verified-commit move
semantics. Jobs are queued in SQLite with fenced claims, checkpoints, retries,
and interrupted-owner recovery, and execute in the background agent — closing
the desktop does not discard accepted work.

**Synchronize and schedule** — Three-way classification with conflict
categories and deletion guards, producing immutable SHA-256 plans. Preview is
read-only; applying requires an explicit approval bound to the plan, verified
roots, and live capabilities. Cron schedules honor time zones and DST and
dispatch preview-only runs.

**SSH terminal** — A managed SSH.NET client with no PuTTY dependency. Sessions
run in the agent with vault-backed authentication and verified host keys. The
VT renderer supports cursor addressing, scroll regions, ANSI, 256-color, and
true-color output with bounded scrollback.

**Themes and settings** — Light, Dark, and System appearances applied across
every window, including native scrollbars, list headers, and edit borders.
Settings cover transfers and sync, editing, appearance, workspaces, rebindable
shortcuts, per-provider connection defaults and trust policy, and updates.
Settings, connections, and sync tasks can be exported and imported, optionally
password-protected.

For what is implemented versus planned, see
[Development status](docs/development-status.md).

## Install

Download the latest release from
[GitHub Releases](https://github.com/clausdk/StorageHub/releases):

- `StorageHub-<version>-win-x64-Setup.exe` — recommended one-click, per-user installer;
- `StorageHub-<version>-win-x64.msi` — per-user MSI for managed deployment; and
- `StorageHub-<version>-win-x64-portable.zip` — self-contained portable payload.

The installer does not require elevation and starts the background agent only as
the signed-in Windows user, so scheduled work runs only while that user is
signed in. Application data under `%LOCALAPPDATA%\StorageHub` is preserved
across updates and uninstalls.

Installed builds check the official GitHub release feed at startup and silently
download integrity-checked updates by default. Settings can disable automatic
checks or downloads, exclude release candidates, or opt into silent
install-and-restart. **Help > Check for Updates...** remains available when
automatic checks are off. Portable and developer builds never modify an
installation.

## Provider status

`CL.Storage` supplies native implementations without PuTTY or external transfer
executables. StorageHub exposes those implementations only through capability
checks; a provider is not complete merely because it exists in the library.

| Provider | In `CL.Storage` | StorageHub profile model | End-to-end StorageHub coverage |
| --- | :---: | :---: | --- |
| Local directories and UNC paths | Yes | Yes | Real integration test |
| Amazon S3 and S3-compatible services | Yes | Yes | Hermetic MinIO interoperability and hostile-input test |
| FTP, explicit FTPS, implicit FTPS | Yes | Yes | Hermetic interoperability, pin/downgrade, and client-PFX tests |
| SFTP | Yes, via SSH.NET | Yes | Hermetic password/key interoperability and hostile host-key tests |
| SSH terminal client | Managed SSH.NET client | Yes | Hermetic open/write/read/resize/close test beside SFTP |
| WebDAV | Yes | Not yet | Pending |
| Azure Blob Storage | Yes | Not yet | Pending |
| Google Cloud Storage | Yes | Not yet | Pending |
| OpenStack Swift | Yes | Not yet | Pending |

Provider expansion order and the bar each one must clear are in the
[roadmap](docs/roadmap.md).

## Build from source

### Prerequisites

- Windows and PowerShell
- Visual Studio C++ build tools with the Desktop development workload, required
  for the Explorer drag/drop broker
- [.NET SDK 10.0.401](global.json), or a later 10.0 patch accepted by `global.json`
- Git
- CPython 3.12 when running the local FTP/FTPS or SFTP fixtures; CI pins 3.12.10

The CodeLogic framework and `CL.Storage` provider library are restored from the
centrally pinned `CodeLogic` and `CodeLogic.Storage` NuGet packages.

### Restore, build, and test

```powershell
dotnet restore StorageHub.slnx --locked-mode
dotnet build StorageHub.slnx --configuration Release --no-restore
dotnet test StorageHub.slnx --configuration Release --no-build --no-restore
dotnet list StorageHub.slnx package --vulnerable --include-transitive --no-restore
```

Warnings are errors, package versions are centrally managed, and StorageHub
projects restore from committed lock files. CI runs the same Release build,
test, and vulnerability-audit path on Windows.

Every successful push to `main` publishes a uniquely versioned prerelease after
the Release build, full test suite, dependency audit, packaging, silent install,
agent health, and silent uninstall checks pass. A failed check produces no
release.

### Provider fixtures

```powershell
.\eng\run-provider-smoke.ps1
```

This starts pinned loopback MinIO, FTP/FTPS, and SFTP services, exercises their
real health/read/write behavior, and runs provider-neutral transfers between
each supported remote direction and Local storage. S3 is verified in both
directions. FTP and SFTP outbound create is verified to fail closed, because
those protocols cannot provide StorageHub's required atomic create-if-absent
guarantee.

### Run from source

Start the background agent first:

```powershell
dotnet run --project src\StorageHub.Agent.Windows --configuration Release
```

Then the desktop shell in another terminal:

```powershell
dotnet run --project src\StorageHub.Desktop.WinForms --configuration Release
```

The agent uses `%LOCALAPPDATA%\StorageHub` by default. Set
`STORAGEHUB_DATA_ROOT` before starting it to use an isolated development data
directory. `--run-once` performs startup and a clean shutdown; `--health` runs
the CodeLogic health path.

## Using StorageHub

File commands act on the pane marked **Active**, outlined in the theme accent
color. Defaults include Ctrl+C/Ctrl+X/Ctrl+V for copy/cut/paste, Ctrl+A for
select all, F2 for rename, Delete for delete, Ctrl+Shift+N for a folder,
Ctrl+Alt+N for an empty file, F5 for refresh, Ctrl+L for the address, and F6 for
the next pane. Text fields and SSH terminal input keep their own keyboard
behavior; file shortcuts never operate on SSH panes.

Use **Tools > Settings > Shortcuts** to reassign commands, clear a binding, or
restore the defaults. Conflicting shortcuts must be cleared before reassignment.

## Documentation

- [Architecture](docs/architecture.md)
- [Security model](docs/security-model.md)
- [Development status](docs/development-status.md)
- [Implementation roadmap](docs/roadmap.md)
- [Release engineering](docs/releasing.md)
- [Contributing](CONTRIBUTING.md)
- [Security policy](SECURITY.md)

## License

StorageHub is licensed under the [MIT License](LICENSE).
