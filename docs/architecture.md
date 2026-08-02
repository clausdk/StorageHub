# Architecture

StorageHub separates file semantics, provider integration, durable execution,
Windows security, and presentation so that a provider SDK cannot define the
application's safety rules.

## Runtime shape

```text
StorageHub.Desktop (WinForms/Krypton)
            |
            | versioned current-user named pipe
            v
StorageHub.Agent.Windows (CodeLogic lifecycle)
       |            |             |
       v            v             v
  SQLite/WAL    DPAPI vault   scheduler/engine seams
                                    |
                                    v
                         StorageHub.Storage contract
                                    |
                                    v
                         CL.Storage runtime adapter
                                    |
                   local / S3 / FTP(S) / SFTP / ...
```

The desktop is intended to be disposable presentation state. The agent owns
long-running work and durable state. It exposes status, request-scoped saved
connection discovery/testing, paged storage listing, optimistic profile CRUD,
durable transfer queue commands, sync preview/apply management, and preview-only
schedule management. A separate current-user-only channel owns secret enrollment
and rotation. The desktop uses these operations for bounded remote browsing,
Connection Manager, saved-pane file transfers, queue control, sync review, and
schedule editing. A dedicated read-only inspector contract exposes bounded object
version pages, portable metadata, and tags; signed URLs and advanced mutations
remain outside that contract.

## Project boundaries

| Project | Responsibility |
| --- | --- |
| `StorageHub.Contracts` | Stable results and versioned IPC data transfer objects |
| `StorageHub.Domain` | Strong IDs, root-safe addresses, entries, and capabilities |
| `StorageHub.Application` | CodeLogic application lifecycle and validated connection-profile model |
| `StorageHub.Storage` | Provider-neutral asynchronous endpoint/session contract |
| `StorageHub.Storage.CodeLogic` | Vault/trust-aware profile connector, runtime-only `CL.Storage` adapter, and streaming write bridge |
| `StorageHub.Transfers` | Transfer intent, state, durable-store contracts, checkpoints, bounded copy, and verified move behavior |
| `StorageHub.Sync` | Three-way classification, deletion policy, immutable plans, execution approvals, and plan execution |
| `StorageHub.Persistence` | SQLite configuration/migrations and durable profile, trust, scheduler, transfer, sync, execution, and outbox stores |
| `StorageHub.Security` | Opaque secret references, vault contracts/envelopes, trust contracts |
| `StorageHub.Infrastructure.Windows` | Windows DPAPI and restricted runtime-secret files |
| `StorageHub.Agent` | Runtime coordination, named-pipe IPC, schedules, and scheduler contracts |
| `StorageHub.Agent.Windows` | CodeLogic console host and database/vault/worker composition, storage browsing, profile/transfer/sync/schedule IPC, and dedicated secret IPC |
| `StorageHub.Desktop.WinForms` | High-DPI dual-pane shell, asynchronous local/remote browsers, saved-pane transfers, queue/sync/schedule management, Connection Manager, and agent-status monitor |
| `StorageHub.Diagnostics` | Allow-list policy for safe diagnostic artifacts |

Core projects target `net10.0`. Windows hosts target Windows-specific TFMs; only
those layers may depend on WinForms or operating-system security APIs.

## Storage contract

Every session is bound to a `ConnectionProfileId` and a root identity. An address
contains that same identity plus a canonical relative path. Validation rejects
absolute paths, parent traversal, mismatched roots, invalid normalization, and
operations against a different profile.

Providers publish effective capabilities. File and directory copy/move are
represented separately because a provider need not support both. Callers must
also preflight range reads, conditional versions, atomic publication, and
resumable upload. An absent capability is a hard unsupported result; adapters
must not silently substitute weaker behavior. CL flags for ACL, lease, and append
are not advertised as usable until StorageHub has corresponding operations.

Expected provider errors cross the boundary as `StorageResult` failures with safe
codes and categories. Cancellation remains cancellation. Unexpected exceptions
are reduced to non-sensitive operation context.

`CodeLogicConnectionProfileConnector` resolves vault references and accepted
trust records immediately before building a provider configuration. It supports
Local, S3 with system trust, acknowledged plaintext FTP, FTPS with system trust
or explicit certificate pins, and SFTP with explicit host-key pins. Credentials
are registered only in memory. PFX/private-key files are materialized in a
current-user-only directory and retained for exactly the runtime connection's
lifetime.

The session root identity is a canonical SHA-256 transcript over the profile ID
and revision, endpoint namespace, authentication mode/principal, opaque vault
reference revisions, and selected trust-record revisions. Credential values are
never included. Rotating credentials or trust invalidates stale pane pages,
queued addresses, and other identity-bound work instead of silently retargeting
it.

FTPS client PFX use requires a separate vault-backed password. SFTP private-key
profiles require a vault-backed passphrase. The connector strictly parses the
OpenSSH, legacy PEM, or PKCS#8 envelope and uses SSH.NET to decrypt and parse the
actual key with that passphrase before creating the runtime backend.

The connector rejects settings that the current provider boundary cannot enforce,
including TOFU capture, S3 certificate pinning, per-connection proxy/bandwidth,
non-UTF-8 FTP names, and incompatible retry/timeout combinations. It never turns
on accept-any certificate or host-key behavior.

`CodeLogicStorageEndpointSession` maps the resulting runtime connection to the
common contract. Uploads use a bounded pipe rather than buffering an entire file.
Local writes stream into a cryptographically random reserved staging object and
publish through a same-volume atomic move; abort, validation failure, and provider
failure clean the staging object. The reserved namespace is rejected and filtered,
and stale owned artifacts are scavenged when a local session is registered.
Disposing the runtime connection removes its backend without persisting provider
configuration through CodeLogic and removes short-lived credential files.

## Transfers and sync

A transfer is described by immutable source/destination addresses and a
verification policy. The executor:

1. validates profiles, roots, capabilities, source metadata, and preconditions;
2. streams through a bounded buffer;
3. commits the destination write;
4. verifies the destination size and, when required and available, its digest;
5. deletes the source for a move only after successful verification.

Checkpoint models bind resume decisions to source identity, size, modification
time, optional portable SHA-256, provider state, and completed parts. The SQLite
transfer store persists complete immutable intents and uses optimistic state
revisions, exclusive fenced claims, renewable leases, monotonic versioned
checkpoints, retry availability, and atomic attempt closure. Its recovery marks
expired in-flight ownership as interrupted while preserving live leases;
migrated legacy in-flight rows without root identity are held for reconciliation.
The Windows agent composes the worker and bounded queue IPC. Manual enqueue
reuses a stable transfer ID after a lost acknowledgement and surfaces unresolved
ambiguity instead of creating an invisible duplicate.

Sync compares both sides with a baseline and classifies creates, modifications,
deletes, equality, and conflicts. Bounded scans can stream SHA-256 for files that
lack both a version ID and ETag; algorithm and byte count are explicit, and
opaque ETags never become hashes. Execution consumes an immutable, canonically
hashed digest-schema-v3 plan. Apply mode verifies the plan digest and, for destructive work,
requires a separate execution-approval digest binding snapshot completeness and
counts, verified roots, live session roots and capabilities, execution mode,
deletion limits, and transfer options. Substitution fails before provider I/O.
Deletes require exact object versions and native conditional versioning;
overwrites additionally require versioned source and destination identities,
complete scans, and native temporary-file/move/atomic-rename support. Providers
that cannot enforce those guarantees return unsupported. Preview mode performs
no endpoint mutations.

The scheduler core polls durable snapshots and relies on the schema-v2 SQLite
store to atomically acquire and renew profile-scoped leases with monotonic fencing
tokens. It bounds global concurrency, prevents overlapping runs for a profile,
samples lease time only after acquiring the cross-process write lock, bounds
renewal calls by the remaining lease, rejects stale completion, records exact
completion retries through the schema-v4 immutable journal, records misfires, and
can retain at most one queued occurrence. The Windows host composes a fenced
runner that records a durable preview outbox event. Schedule management is
intentionally preview-only: a schedule cannot create an execution approval or
request unattended provider mutation.

## Persistence and recovery

The SQLite initializer enables foreign keys, WAL journaling, `synchronous=FULL`,
a bounded busy timeout, cross-process-serialized ordered migrations, and `quick_check`. Database writes go
through a single-writer gate. Schema v1 establishes normalized state for
connections, credential references, trust, transfers/checkpoints, sync plans and
runs, schedules, conflicts, notifications, audits, settings, plugins, and an
outbox. Schema v2 adds scheduler CAS/lease/fencing/queue/outcome state; schema v3
adds immutable transfer intents plus transfer revision/retry/lease metadata;
schema v4 adds immutable lease-keyed scheduler completion records; schemas v5-v7
add sync durability, orchestration, and fenced execution; and schema v8 adds
portable checksum evidence plus digest-schema tracking.

Connection-profile and trust repositories use optimistic versions. Scheduler,
transfer, sync, execution, and outbox repositories implement their durable
concurrency protocols. Transfer, sync-outbox, and scheduler workers are composed
into the agent; schema presence alone is still not treated as feature completion.

Initialization can report recovery-only operation instead of starting mutating
subsystems after a database failure. The Windows host composes real database,
vault, transfer, sync-outbox, scheduler, and IPC health checks under the CodeLogic
lifecycle.

## Dependency and extension policy

Provider SDKs stay behind `StorageHub.Storage`. StorageHub does not shell out to
PuTTY, WinSCP, FileZilla, rsync, or other transfer executables. `CL.Storage` uses
managed/open-source provider packages such as SSH.NET and FluentFTP.

The source-integrated `CL.Storage` dependency is pinned to CodeLogic.Libs commit
`25d357b7e3896e2ff3d6c29875178a6b0f12ed60`. An MSBuild pre-build target resolves
the checkout's Git `HEAD` and rejects source-dependent builds at any other
revision.

New providers should first join `CL.Storage`, then receive a StorageHub profile
model, secure credential/trust mapping, capability conformance tests, and a
hermetic integration test. A provider is not complete until all four layers are
present.

See [Development status](development-status.md) for the current integration
frontier and [Security model](security-model.md) for trust boundaries.
