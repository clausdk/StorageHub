# Security policy

StorageHub handles credentials and destructive file operations. Please report
security issues privately and give maintainers time to investigate before public
disclosure.

## Supported versions

StorageHub has not published a stable release yet. Security fixes are made on the
current default branch only.

| Version | Supported |
| --- | --- |
| Current default branch | Yes |
| Old commits and local preview builds | No |

## Report a vulnerability

Use the repository's **Security** tab and select **Report a vulnerability** to
open a private GitHub Security Advisory. If that option is unavailable, contact
a maintainer privately through the repository owner profile. Do not include
credentials or private keys in an issue, discussion, pull request, log, or test
fixture.

Include, when possible:

- the affected commit and component;
- impact and realistic attack preconditions;
- minimal reproduction steps or a proof of concept using synthetic data;
- whether the issue can expose secrets, cross a connection root, bypass TLS/SSH
  trust, corrupt durable state, or cause an unintended overwrite/delete;
- suggested mitigations, if known.

Maintainers aim to acknowledge a complete report within five business days.
Timelines for validation, remediation, and disclosure depend on severity and on
coordination with upstream providers.

## High-priority issue classes

- Secret material written to SQLite, JSON, logs, diagnostics, crash artifacts,
  command lines, or normal IPC.
- Path traversal, root-identity confusion, or an operation escaping its selected
  endpoint root.
- TLS certificate or SSH host-key validation bypasses, unsafe trust-on-first-use
  behavior, or silent fallback to plaintext transport.
- Sync-plan approval bypass, deletion-threshold bypass, source deletion before a
  verified move commit, or unsafe resume after source mutation.
- Named-pipe authentication, framing, sequencing, or protocol-negotiation flaws.
- SQLite migration, lease-fencing, or optimistic-concurrency errors that permit
  duplicate or stale destructive work.
- Vulnerabilities in CodeLogic, `CL.Storage`, provider SDKs, or native SQLite that
  are reachable from StorageHub.

## Safe handling during research

Use temporary directories, disposable buckets/containers, synthetic secrets,
and isolated accounts. Do not test against systems or data you do not own or have
explicit permission to use. Avoid denial-of-service testing against public
services. Remove any captured secret material after the report is delivered.

StorageHub's implemented controls and known engineering-preview boundaries are
described in [the security model](docs/security-model.md). A security boundary
described there is a design invariant, not a claim that this preview has received
an independent security audit.
