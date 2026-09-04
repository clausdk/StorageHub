# Development status

StorageHub is an engineering preview. This page distinguishes implemented and
tested foundations from UI concepts and planned production integration.

## Implemented and tested

| Area | Current capability |
| --- | --- |
| Domain/contracts | Strong identifiers, root-safe canonical addresses, entries, granular capabilities, structured results, bounded/versioned IPC DTOs |
| CodeLogic lifecycle | Application configuration, localization, startup/stop, recovery state, aggregate health |
| Storage adapter | Vault/trust-aware profile binding, runtime `CL.Storage` sessions, capability/error mapping, bounded streaming upload, portable streamed SHA-256, exact-version/metadata/tag adapters, atomic local staging/publish with stale-orphan cleanup, local-provider integration test, and hermetic MinIO/S3, FTP/FTPS, and SFTP fixtures |
| Profiles | Local, S3, FTP, FTPS, SFTP models; metadata; vault references; transport/trust policy validation; SQLite CRUD/search/soft-delete |
| Security | Versioned vault, Windows DPAPI current-user protector, secret leases, trust-store contracts, SQLite repository, and atomic trust rollover |
| Persistence | Cross-process-serialized ordered migrations; strict transactional archival of the recognized legacy-preview schema collision; WAL/foreign-key/full-sync configuration; integrity checks; single-writer boundary; profile/trust, scheduler, transfer, sync, execution, and reliable-outbox repositories through schema v8 |
| Transfers | State machine, immutable v3 intent/checkpoint models, bounded copy, portable SHA-256/length verification, safe move ordering, durable fenced queue with retries/recovery, running agent worker, mutation/query IPC, saved-pane file enqueue, and bounded recursive saved-connection folder copy with immutable manifests and empty-directory preservation |
| Sync | Three-way classifier, conflict categories, deletion guards, immutable digest-schema-v3 plans, durable profiles/baselines/plans/runs/conflicts, preview/apply orchestration, execution fencing, leased outbox worker, and desktop management |
| Scheduling | Cron/time-zone/DST calculation, misfire decisions, SQLite optimistic revisions, profile-scoped fenced leases, post-lock authoritative timing, bounded renewal, idempotent/stale-completion handling, queue-one behavior, preview-only durable dispatch, management IPC, and desktop editor |
| Agent | Guarded reparse-free/current-user-only data tree, one process per Windows user, real database/vault startup, protected CodeLogic discovery, transfer/sync/scheduler workers, health reporting, bounded normal and secret-only current-user pipes, browse/test/profile/trust/queue/sync/schedule/read-only-object-inspector commands, sanitized vault enrollment/rotation/deletion, profile-bound trust enrollment/rejection/rollover, and bounded unauthenticated SSH host-key discovery that never records trust |
| Desktop | Dual-pane workspace tabs with direct close controls, active-pane navigation/history/bounded paging, saved-connection file copy/move enqueue, durable queue/sync/schedule surfaces, read-only versions/metadata/tags inspector, structured General/Connections/Updates/About settings, protocol-aware Connection Manager with provider-enforceable operational defaults, revision-bound connection-health presentation, a grouped/sorted saved-profile tree, searchable tag pills, manual/ask/automatic SSH host-key discovery, verified certificate/host-key enrollment, rejection, and rollover, explicit vault enrollment actions, agent status polling, and persisted automatic-update controls; actions without an implemented controller or persistence path remain hidden |
| Packaging | Self-contained win-x64 desktop/agent payload, per-user Velopack Setup and MSI, portable archive, graceful agent lifecycle, fixed-source GitHub release checking, integrity-checked silent update/restart, checksums, provenance attestation, disposable-runner smoke test, and prerelease publication after every successful main push |
| Diagnostics | Safe artifact manifest policy that excludes secret and durable-state files |

The automated suite includes unit and contract coverage, local-browser and
WinForms construction tests, named-pipe integration, durable SQLite scheduler,
transfer, sync, execution, and outbox concurrency/recovery tests, portable hash
evidence tests, a real local `CL.Storage` integration fixture, and a disposable
MinIO fixture covering S3-compatible bounded round trips, Unicode, atomic
create-only conflicts, abort, mounted-prefix containment, address/token
substitution, and sanitized wrong-credential rejection.
Hash-locked pyftpdlib fixtures add bounded FTP/FTPS transfer, listing, abort,
mount/address containment, explicit and implicit TLS, generated certificate-pin
acceptance/rejection, self-signed system-trust rejection, no-downgrade behavior,
and generated client-PFX control-channel authentication. FTP's lack of atomic
conditional create is asserted before provider I/O rather than weakened.
Hash-locked AsyncSSH endpoints add SFTP password and encrypted OpenSSH-key
round trips, host-key acceptance and rotation rejection, authentication-mode
substitution, wrong credentials/passphrases/keys, malformed and missing pins,
mount/address containment, abort, capability-aware create-only rejection, and
real unauthenticated host-key discovery with exact SHA-256 comparison.
Desktop shell regressions additionally verify workspace close/reopen behavior,
reject provider example cards as saved profiles, exercise deterministic
favorite/folder/provider/disabled grouping and tag-aware search, and require
every visible main or Connection Manager toolbar action to have a real handler.

## Integration work still required

1. Extend the implemented recursive saved-connection folder copy to deliberately
   modeled local/ad-hoc sources and durable dependency-aware folder moves.
   Existing local destinations without an atomic version/ETag condition remain
   create-only.
2. Expand persisted settings beyond the implemented updater and SSH-discovery
   preferences, and add a bounded temporary-session contract before restoring Quick Connect.
   Import/export, rename, pane comparison, and global queue controls likewise
   remain hidden until their real command paths and failure handling exist.
3. Add periodic refresh policy and bounded session leasing to the implemented
   revision-bound, in-memory connection-health snapshots. Aggregate CodeLogic
   health intentionally reports CL.Storage's disabled configuration bootstrap
   and is not a substitute for provider health.
4. Add crash/restart, credential/trust rotation, cancellation, low-disk,
   long-path, Unicode, large-directory, and lost-acknowledgement stress tests.
5. Expand profiles in order through WebDAV, Azure Blob, Google Cloud Storage,
   and OpenStack Swift, preserving provider-specific credential and trust rules.
6. Finish Authenticode installer/update signing, accessibility review,
   localization, telemetry consent, and stable-release/recovery documentation.

## Provider expansion order

StorageHub's persisted profile model currently covers Local, S3, FTP, FTPS, and
SFTP. WebDAV, Azure Blob, Google Cloud Storage, and OpenStack Swift already exist
in `CL.Storage`, but each still needs a StorageHub profile, secure credential and
trust mapping, capability conformance tests, and a hermetic integration fixture.

Provider work should not bypass the common contract. If a native provider feature
cannot be represented safely, extend the capability/operation model first and
make older adapters return an explicit unsupported result.

## Release gate

The project should not be labeled stable until:

- the desktop-to-agent happy path works for every advertised core provider;
- queued transfers and sync runs survive process restart without duplicate or
  stale destructive work;
- all transport identity policies are exercised against positive and negative
  integration fixtures;
- upgrade, recovery, vault rotation, and uninstall behavior are documented and
  tested;
- Release build, tests, dependency audit, and packaging run reproducibly in CI;
- installer upgrade/rollback coverage and Authenticode signing are production-ready;
- an independent security review has addressed secret, path, IPC, trust, and
  destructive-operation boundaries.
