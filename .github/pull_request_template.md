## Summary

Describe the user-visible outcome and the narrow scope of this change.

## Safety and compatibility

- [ ] I considered credentials, trust decisions, root/path boundaries, overwrites, deletes, resume behavior, IPC contracts, and database migrations where relevant.
- [ ] Destructive or security-sensitive behavior remains fail-closed and has focused tests.
- [ ] This change does not add secrets, private endpoints, vault/database files, keys, certificates, dumps, provider traces, or unsanitized logs.
- [ ] Any new dependency is necessary, actively maintained, license-compatible, and recorded through central package management.

Explain applicable risks, compatibility effects, and mitigations. Write `Not applicable` only when none of the areas above are affected.

## Verification

List the exact build/test commands run and their results. Include focused tests for the changed layer.

```text
Commands and results
```

## Documentation

- [ ] Relevant architecture, security-model, development-status, or operator documentation is updated.
- [ ] No documentation change is needed; explain why below.

StorageHub is an engineering preview. This pull request does not imply stable support for a provider or workflow beyond the behavior covered by the repository's tests and documentation.
