# Security model

This document describes StorageHub's intended and currently implemented security
boundaries. It is not an independent audit or a guarantee that the engineering
preview is free of vulnerabilities.

## Assets and trust boundaries

StorageHub protects:

- credentials, tokens, private keys, PFX files, and passphrases;
- connection endpoints and server-identity decisions;
- user file contents and metadata;
- approved transfer/sync intent and durable execution state.

Remote providers and their responses are untrusted. Paths, pagination tokens,
metadata, errors, certificates, and SSH host keys must be validated before they
affect local state. Local desktop IPC clients are also treated as protocol input,
even though the Windows pipe is restricted to the current user.

The current-user Windows account is a trust boundary. DPAPI deliberately allows
the same Windows user context to decrypt its own StorageHub secrets. StorageHub
does not claim to defend against an attacker who already has arbitrary code
execution as that user.

## Implemented controls

### Secret storage

- SQLite profiles store opaque references, never plaintext secret values.
- Secret envelopes are protected by Windows DPAPI in current-user scope with
  StorageHub-specific entropy that authenticates envelope identity and version.
- Vault updates use write-through temporary files and atomic replacement.
- Opening a secret returns a disposable lease; managed buffers are zeroed on
  disposal and on error paths.
- Envelope sizes, reference syntax, versions, timestamps, and protection scheme
  are bounded and validated.
- PFX and SSH-key fields in connection models are vault references. The Windows
  materializer writes provider-required files into a non-UNC directory with
  inherited access disabled and access granted only to the current user. Files
  are hidden, not content-indexed, and removed on a best-effort basis with the
  runtime connection.
- An FTPS client-certificate PFX requires a separate vault-backed password
  reference. SFTP private-key authentication requires a vault-backed passphrase,
  accepts only strict OpenSSH, legacy PEM, or PKCS#8 envelopes, and requires
  SSH.NET to decrypt and parse the actual key with that passphrase before the
  provider receives it.

Windows backup or account recovery must include the account context capable of
decrypting DPAPI data. Copying the vault directory alone to a different user or
machine is not a supported recovery mechanism.

### Endpoint identity and transport

- FTPS and S3 HTTPS profiles carry a non-unspecified TLS policy.
- SFTP profiles cannot use unspecified host trust. The current runtime connector
  requires an explicit stored host-key pin and rejects trust-on-first-use because
  the provider boundary cannot capture a candidate key safely.
- Trust records normalize host, port, artifact kind, and SHA-256 fingerprint and
  use optimistic versions.
- Connection Manager trust mutations name a saved profile and its exact revision;
  the agent derives the enforceable artifact kind, canonical host, and port from
  that pinned profile. Enrollment and rejection are optimistic, while rollover
  atomically revokes the old identity and trusts the verified replacement.
- Plain FTP and HTTP S3 endpoints require an explicit insecure-transport
  acknowledgement in the domain model.
- Endpoint and proxy URLs reject embedded credentials, query strings, and
  fragments. Proxy credentials use a separate reference.
- The profile model never interprets an SSH private key as PFX; PFX is reserved
  for FTPS client certificates.

The runtime connector resolves secrets and trust just before in-memory
`CL.Storage` registration. It supports FTPS system trust or explicit certificate
pins and requires explicit stored pins for SFTP. S3 certificate pinning and TOFU
capture are rejected because the current provider boundary cannot enforce them.
Provider `AcceptAnyCertificate` and `AcceptAnyHostKey` options remain disabled.
Unsupported proxy, bandwidth, filename-encoding, retry, and timeout combinations
also fail closed.

Each opened session receives a root identity derived from a canonical SHA-256
transcript containing the profile ID/revision, endpoint namespace,
authentication mode/principal, opaque vault reference revisions, and selected
trust-record revisions. Credential bytes are never included. A profile, secret,
or trust rotation therefore invalidates stale identity-bound work. CL.Storage
does not expose the resolved IAM principal for S3's default credential chain, so
that mode binds the profile revision and authentication mode but cannot bind the
runtime IAM identity.

### Paths and destructive operations

- Storage addresses are relative to both a connection profile and a stable root
  identity; traversal and cross-root operations are rejected.
- Transfer and sync operations preflight profile/root/capability invariants.
- A move removes its source only after destination commit and verification.
- Portable checksum evidence always names SHA-256 and records its byte count.
  Provider ETags and algorithm-less checksum fields are treated as opaque
  identity tokens, never as content hashes.
- Sync apply verifies the immutable plan digest. Destructive apply additionally
  requires a canonical execution-approval token bound to scan completeness and
  counts, exact scan-time and live roots, effective provider capabilities,
  execution mode, deletion limits, and overwrite/buffer options.
- Deletion policies can block incomplete inventories, absolute/percentage
  thresholds, root-like deletes, and unapproved destructive plans before I/O.
- Destructive sync deletes require the exact planned object version and native
  conditional versioning. Overwrites require versioned source and destination
  identities, complete scans, verified roots, and native destination
  staging/move/atomic-rename/versioning support. Missing guarantees return
  unsupported before I/O.
- Unsupported version preconditions and resume behavior fail rather than silently
  degrading.
- Atomic create-new is advertised by Local and by CL.Storage's S3 upload path,
  which uses the provider's conditional create request. FTP(S) and SFTP remain
  unsupported for create-only publication. S3 native server-side copy still has
  check-then-copy semantics, so the durable transfer engine keeps its
  identity-safe streaming path.
- Local create and overwrite streams publish only after completed upload via a
  same-volume atomic move from StorageHub's reserved internal staging tree. The
  namespace is rejected from normal operations, filtered from listings, and
  scavenged for stale owned artifacts at local-session registration.
- A pre-read SHA-256 is not an atomic compare-and-swap condition. Replacing an
  existing local destination that has neither a version ID nor ETag remains
  blocked even when both contents can be hashed.

### Durable state and concurrency

- SQLite uses foreign keys, WAL, full synchronous durability, migration history,
  integrity checks, and a single-writer boundary.
- Connection and trust updates use optimistic versions.
- The schema-v2/v4 SQLite scheduler store implements optimistic revisions, atomic
  lease acquisition, monotonic fencing, authoritative time sampled after the
  cross-process writer lock, bounded renewal, stale-completion rejection, exact
  completion idempotency, no active profile overlap, and at most one queued
  occurrence.
- The schema-v3 SQLite transfer store implements immutable intents, exclusive
  fenced claims, lease renewal, optimistic transitions, monotonic checkpoints,
  retry timing, atomic attempt completion, and recovery of expired in-flight
  owners. Legacy in-flight rows lacking root identity are quarantined for
  reconciliation instead of being made executable.
- Schemas v5-v7 persist sync baselines/plans/runs/conflicts, a leased reliable
  outbox, and fenced execution state. Schema v8 records portable checksum
  evidence and plan digest-schema versions while keeping legacy plan/intent
  readers bounded and explicit.

The Windows host composes the transfer worker, sync outbox worker, and scheduler.
Manual enqueue treats the transfer ID as an idempotency key: after a lost
acknowledgement it retries the exact request once and then exposes the ambiguous
ID for queue reconciliation. Scheduled occurrences can only dispatch fenced,
durable preview requests. They cannot manufacture a destructive execution
approval or trigger unattended apply.

### Local IPC and diagnostics

- On Windows, agent and client named pipes use `CurrentUserOnly` and bounded
  account-scoped names. The `StorageHub.Agent.v1.user-<account-hash>` and
  `StorageHub.Agent.Secrets.v1.user-<account-hash>` suffix is the first 128 bits
  of SHA-256 over the current account SID's binary form; the raw SID is never
  placed in a pipe name.
- A fixed per-user file lease prevents two agents, even with different data-root
  overrides, from serving the same pipe. The complete configured data tree and
  CodeLogic discovery directory reject reparse points and receive verified,
  non-inherited current-user-only ACLs before framework or database startup.
- IPC uses a version handshake, bounded length-prefixed JSON frames, request IDs,
  and monotonic message sequences.
- Saved-connection discovery, connection testing, and paged storage listing are
  request-scoped read-only commands. Browse responses omit secret references and
  raw provider metadata, enforce negotiated bounds/root scope, and sanitize
  failures.
- A separate read-only inspector returns only bounded version summaries, portable
  metadata, and portable tags for an exact echoed profile/root/path identity. It
  has no signed-URL, write, or delete message type and rejects mixed failure/data
  responses, non-UTC timestamps, oversized tokens, and cross-root substitutions.
- Transfer queue, sync orchestration, and preview-only schedule operations use
  separately versioned and bounded message contracts. Optimistic revisions,
  stable transfer IDs, immutable plan digests, and execution approval digests
  remain intact across IPC.
- Versioned profile get/create/update/soft-delete commands use optimistic
  revisions and accept only validated provider documents whose credential fields
  are opaque vault references. They contain no secret-value or free-form notes
  member; existing notes never cross profile IPC.
- Versioned trust get/decide/rollover commands expose only bounded non-secret
  identity history. They reject stale profile or trust revisions, non-pinned
  profiles, malformed fingerprints, cross-record identity substitution, and
  same-identity rollover before changing the authoritative store.
- Secret-prefixed messages are rejected on the normal IPC channel. Enrollment,
  rotation, and deletion use the separate account-scoped secret pipe,
  typed secret envelopes, the 32 MiB secret-frame ceiling, and a 16 MiB material
  ceiling. That pipe is Windows/current-user-only; serialized frame buffers,
  desktop transport copies, and agent request buffers are zeroed after use.
- Profile and vault failures are mapped to bounded stable messages. Provider,
  repository, and vault exception details are never returned over IPC.
- Concurrent clients and connect/handshake/idle/request times are bounded.
- Diagnostic manifests reject traversal, database/WAL files, vaults, keys,
  certificates, dumps, credential-like names, and provider/wire traces.

## Data locations

The Windows agent stores state below `%LOCALAPPDATA%\StorageHub` unless
`STORAGEHUB_DATA_ROOT` is set. That override is intended for isolated development
and testing. Startup rejects UNC, volume-root, and reparse paths and replaces and
verifies ACLs on the complete selected tree. Do not point the override at a
directory containing unrelated data or place it in a cloud-synchronized folder.

Generated diagnostic bundles and exports must be derived through explicit
allow-list policy. Never attach the raw data directory to a bug report.

## Known preview limitations

- S3-compatible storage is exercised against a disposable,
  SHA-256-pinned MinIO process on random loopback ports with ephemeral
  credentials; the fixture rejects non-loopback endpoints and verifies mounted
  prefix containment, cross-profile/root substitution, hostile tokens,
  create-only collision safety, abort behavior, and sanitized credential
  failures. FTP and explicit/implicit FTPS use hash-locked user-space servers,
  random loopback control/passive ports, generated credentials and short-lived
  CA/server/client certificates, encrypted data channels, an encrypted server
  key, and an encrypted client PFX. The CA private key remains memory-only.
  Negative cases cover wrong pins, private-CA system trust, missing
  or invalid client-PFX authentication, bad credentials, and plaintext/TLS
  downgrade attempts without logging secrets. SFTP uses hash-locked AsyncSSH
  endpoints on random loopback ports with per-run password, encrypted RSA host
  keys, encrypted authorized and unauthorized client keys, and mounted roots.
  It covers password-only and public-key-only authentication, changed host keys,
  malformed/missing pins, wrong passwords/passphrases/keys, authentication-mode
  substitution, bounded transfer/listing/abort behavior, and root/address
  containment. Fixture output is drained and its exact processes, key files,
  data, and run directory are removed after the test.
- The pinned CL.Storage local provider validates reparse-point containment before
  later path-based I/O; it does not yet use handle-relative no-follow traversal.
  Until that upstream race is closed, use local provider roots that are not
  writable by another security principal. The current-user threat boundary does
  not treat concurrently hostile local filesystem mutation as safe. StorageHub's
  random reserved staging ownership and cleanup rely on that same boundary.
- Connection Manager requires users to obtain and verify certificate and SSH
  host-key SHA-256 fingerprints through a separate trusted channel. StorageHub
  does not probe or accept a candidate identity on first contact; pinned profiles
  remain fail-closed until the verified fingerprint is explicitly saved.
- Manual pane transfer currently accepts files from saved connections only;
  directory recursion and ad-hoc/This PC queue identities are not yet modeled.
- Aggregate CodeLogic health intentionally sees CL.Storage's persisted-provider
  bootstrap as disabled. Per-connection health/session caching is still pending;
  aggregate health must not be presented as a successful remote probe.
- Scheduled sync is preview-only. An apply still requires an explicit immutable
  approval through the sync workflow.
- The project has not completed an external penetration test or security audit.

Until these boundaries are closed, use disposable test data and keep an
independent backup. Report suspected weaknesses using [SECURITY.md](../SECURITY.md).
