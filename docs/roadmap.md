# StorageHub implementation roadmap

StorageHub's goal is a professional Windows file manager that combines local
exploration, secure remote connections, durable transfers, synchronization, and
automation without delegating execution to PuTTY, WinSCP, FileZilla, or `rsync`.
The WinForms application is one presentation layer; storage, transfer, sync, and
scheduling remain reusable outside Windows-specific projects.

This roadmap is ordered by safety dependency, not by menu visibility. A provider
SDK feature is not a StorageHub feature until its identity, capability, durable
state, IPC, UI, and failure behavior are all represented and tested.

## Non-negotiable invariants

- Credentials, tokens, private keys, PFX material, and passphrases remain in the
  current-user vault or short-lived restricted files. They never enter profile
  JSON, normal IPC responses, logs, diagnostics, or root-identity digests.
- Every provider operation is scoped to a saved profile and a revision-bound root
  identity. Path traversal, stale roots, and cross-profile substitution fail.
- Copy, move, overwrite, delete, resume, and scheduled apply are separate safety
  decisions. A provider's broad “supported” flag never authorizes a weaker
  implementation.
- The agent owns durable and long-running work. Closing the desktop does not erase
  accepted jobs, and a lost IPC acknowledgement does not create a second job ID.
- Preview is read-only. Mutation requires an immutable plan, current evidence,
  explicit policy/approval, and provider primitives capable of enforcing it.
- Provider ETags are opaque mutation tokens. Only an explicitly named streamed
  digest such as SHA-256 is content-hash evidence.

## Delivery tracks

### 1. Secure foundation — implemented

- .NET 10 solution boundaries and CodeLogic lifecycle.
- Provider-neutral storage sessions and root-safe addresses.
- DPAPI current-user vault, trust records, and restricted runtime key/PFX files.
- Local, S3, FTP/FTPS, and SFTP profile validation and CL.Storage binding.
- SQLite WAL database, ordered migrations, integrity/recovery mode, and
  single-writer coordination.
- Current-user-only bounded named-pipe protocol and separate secret channel.

### 2. Durable working core — implemented

- Dual-pane local/remote browsing with saved connections and bounded paging.
- File copy/move enqueue between saved-connection panes.
- Fenced transfer worker with retries, cancellation, checkpoints, recovery, and
  queue query/mutation UI.
- Durable sync profiles, baselines, immutable plans, runs, conflicts, execution
  state, reliable outbox, preview/apply commands, and desktop management.
- Cron/time-zone/DST scheduler with fenced preview dispatch and a preview-only
  schedule manager.
- Portable bounded SHA-256 evidence for files without stable provider identity.
- Granular file/directory copy and move capabilities.

### 3. Object insight and connection health — current

- Implemented: read-only object inspector for bounded version pages, metadata,
  and tags, with no signed-URL or mutation messages.
- Exact-version download/read actions that preserve version identity.
- Bounded cached health snapshots per saved connection, including last probe time,
  latency, safe status, and credential/trust-action requirements.
- Short-lived session leasing so navigation and inspection do not repeatedly
  register a provider while still respecting credential/trust rotation.
- Signed URLs as a separate secret-bearing workflow with short defaults, explicit
  copy/reveal actions, expiry display, and no persistence or diagnostics capture.

### 4. Provider expansion

Each provider must add profile/domain models, vault/trust mapping, SQLite
serialization, IPC documents, connector/session composition, Connection Manager
UI, capability conformance tests, and a hermetic fixture.

1. WebDAV: None/Basic/Bearer/current-Windows authentication, HTTPS policy, and
   explicit host/port-bound certificate pins.
2. Azure Blob: connection string, DefaultCredential opt-in, Shared Key, and SAS;
   reject clear-text endpoints hidden inside connection strings.
3. Google Cloud Storage: explicit ADC opt-in or vault-backed service-account JSON;
   private material is runtime-only.
4. OpenStack Swift: Keystone v3 password or static token over HTTPS, with
   session-scoped token refresh.

No provider is marked complete until positive, negative, cancellation, paging,
Unicode, large-object, and hostile-identity tests pass.

### 5. File-manager depth

- Safely enumerated directory jobs with immutable manifests, explicit symlink
  policy, collision preview, and restartable per-file children.
- Create folder, rename, duplicate, delete, trash/restore where supported, batch
  rename, checksums, compare panes, and conflict-resolution workflows.
- Local shell integration, drag/drop, clipboard formats, Open With, properties,
  hidden-file policy, and long-path-aware navigation.
- Bounded metadata index for search, saved searches, duplicate discovery, and
  content-independent filtering. Pane filtering must not be mislabeled as remote
  provider search.
- Provider-native metadata/tag editing only with enforceable concurrency; ACL,
  lease, and append UI only after dedicated StorageHub operation contracts exist.

### 6. Automation

- Dry-run reports and notifications for every scheduled profile.
- Explicit persisted automation policy distinct from an interactive apply token,
  with deletion budgets, allowed roots, time windows, bandwidth/concurrency
  limits, credential/trust revision binding, and an emergency global pause.
- Revalidation immediately before every irreversible publish/delete. Providers
  without atomic conditions remain preview/create-only.
- Retry/backoff, no-overlap/queue-one, missed-run policy, battery/network rules,
  Windows startup/service options, and auditable run history.
- Import/export of non-secret connection and task definitions; secret migration is
  an explicit protected workflow.

### 7. Production readiness

- Hermetic provider matrix plus crash/restart, low-disk, network-loss, lost-ack,
  clock/DST, credential rotation, trust rollover, Unicode, long-path, and
  million-entry inventory tests.
- Accessibility and keyboard review, high-contrast support, localization, DPI and
  multi-monitor testing, reduced-motion behavior, and screen-reader labels.
- Signed installer/update channel, rollback/recovery tooling, database backup and
  export documentation, SBOM, dependency/license audit, and reproducible CI.
- Independent threat-model review, penetration test, and remediation before a
  stable label.

## Definition of done for a user-visible command

A command is enabled only when the selected item kind and live endpoint
capabilities match. It validates canonical paths and exact roots, has bounded
cancellation-aware IPC, returns sanitized structured failures, persists durable
intent before background execution, survives restart without duplicate effects,
emits safe audit/progress state, has keyboard/accessibility coverage, and passes
both positive and adversarial tests.

The current implementation frontier and known limitations are maintained in
[Development status](development-status.md); security reasoning is documented in
[Security model](security-model.md).
