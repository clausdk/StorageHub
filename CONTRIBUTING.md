# Contributing to StorageHub

Thank you for helping build StorageHub. The project favors explicit safety
invariants and verified behavior over provider-specific shortcuts.

## Before you start

Read the [architecture](docs/architecture.md),
[security model](docs/security-model.md), and
[development status](docs/development-status.md). For security-sensitive defects,
follow [SECURITY.md](SECURITY.md) instead of opening a public issue.

Development currently requires Windows, PowerShell, the .NET SDK selected by
[global.json](global.json), and a local checkout of
[Media2A/CodeLogic.Libs](https://github.com/Media2A/CodeLogic.Libs). The complete
solution directly references `CL.Storage.csproj`.

## Set up the repository

The default layout is:

```text
%USERPROFILE%\Documents\GitHub\
|-- StorageHub\
`-- CodeLogic.Libs\
    `-- CL.Storage\CL.Storage.csproj
```

If your repositories live elsewhere, define the paths per invocation:

```powershell
$clRoot = 'C:\src\CodeLogic.Libs'
$clProject = Join-Path $clRoot 'CL.Storage\CL.Storage.csproj'

dotnet restore StorageHub.slnx --locked-mode `
  -p:CLStorageProjectPath="$clProject" `
  -p:CodeLogicLibsRoot="$clRoot"
```

Do not commit a machine-specific path or a locally packed replacement for
`CL.Storage`.

## Make a change

Keep dependencies pointing inward:

1. Contracts and domain types remain independent of UI, SQLite, Windows, and
   provider SDKs.
2. `StorageHub.Storage` defines the endpoint boundary; provider behavior belongs
   in an adapter such as `StorageHub.Storage.CodeLogic`.
3. Windows-only behavior belongs in `StorageHub.Infrastructure.Windows`, the
   Windows agent host, or the desktop project.
4. Secret bytes never enter profile JSON, SQLite, logs, diagnostics, exception
   messages, or normal IPC messages.

For a feature or bug fix:

1. Add or adjust the smallest relevant test first.
2. Implement against capabilities, not provider names, unless configuration is
   genuinely provider-specific.
3. Preserve cancellation and return structured storage failures for expected
   endpoint errors.
4. Treat deletes, overwrites, trust changes, and resume as fail-closed paths.
5. Update documentation when contracts, security assumptions, supported
   providers, or operator behavior changes.

The repository enables nullable analysis, current .NET analyzers, deterministic
builds, and warnings-as-errors. Avoid suppressions unless the change explains why
the invariant is safe.

## Verify locally

Run the same checks as CI from the repository root:

```powershell
$clRoot = 'C:\src\CodeLogic.Libs'
$clProject = Join-Path $clRoot 'CL.Storage\CL.Storage.csproj'

dotnet restore StorageHub.slnx --locked-mode `
  -p:NuGetAudit=true -p:NuGetAuditMode=all `
  -p:CLStorageProjectPath="$clProject" `
  -p:CodeLogicLibsRoot="$clRoot"

dotnet build StorageHub.slnx --configuration Release --no-restore `
  -p:CLStorageProjectPath="$clProject" `
  -p:CodeLogicLibsRoot="$clRoot"

dotnet test StorageHub.slnx --configuration Release --no-build --no-restore `
  -p:CLStorageProjectPath="$clProject" `
  -p:CodeLogicLibsRoot="$clRoot"

$env:CLStorageProjectPath = $clProject
$env:CodeLogicLibsRoot = $clRoot
dotnet list StorageHub.slnx package --vulnerable --include-transitive --no-restore
```

Add focused tests for the changed layer. Remote-provider work should include a
hermetic container or emulator test where practical; tests must never depend on
personal credentials or a public production endpoint. UI changes should retain
keyboard access, accessible names, high-DPI behavior, and construction tests.

## Pull requests

A useful pull request:

- has a narrow, descriptive title and explains the user-visible outcome;
- identifies destructive-operation, credential, trust, migration, and recovery
  implications;
- includes verification commands and their results;
- updates lock files only when dependency inputs change;
- contains no secrets, private endpoint names, database files, vault files,
  keys, certificates, dumps, or provider wire traces.

Protocol and persistence changes need special care. IPC breaking changes require
a protocol-major decision and compatibility tests. Database migrations must be
forward-only, transactional where SQLite permits it, and safe to re-run after an
interrupted startup.

By contributing, you agree that your contribution is licensed under the
[MIT License](LICENSE).
