# Security Policy

## Supported versions

Before 1.0, only the latest `0.x` release receives fixes, security or otherwise. Under semver's
`0.y.z` rule any minor may break, so a fix ships in the next release and upgrading is the
remedy. Older minors are not patched.

From 1.0, the latest minor of each supported major receives fixes, backported through
`release/X.Y` branches (see [docs/releasing.md](docs/releasing.md#patching-an-older-line)).

| Version | Supported |
|---|---|
| Latest `0.x` | Yes |
| Older `0.x` | No, upgrade |

Pre-release (`-beta`, `-alpha`, `-rc`) versions are not covered.

## Reporting a vulnerability

**Please do not open a public GitHub issue for a security vulnerability.**

Use [GitHub's private vulnerability reporting](https://docs.github.com/en/code-security/security-advisories/guidance-on-reporting-and-writing/privately-reporting-a-security-vulnerability)
for this repository. The maintainer will acknowledge the report within a reasonable
time and coordinate a fix and disclosure.

If you cannot use that flow, email **security@codest.be** instead. Please do not
include exploit details in a channel you are not sure is private.

## Scope

This library processes data you provide; it does not make outbound network requests on
its own (beyond the PostgreSQL connection you configure). Security issues in the
following areas are in scope:

- SQL injection or privilege escalation through the schema/tenant-ID validation logic
- Broken authentication or authorisation in multi-tenant query isolation
- Denial-of-service through crafted event payloads
- Sensitive data written to logs or telemetry spans

Issues in third-party dependencies should be reported to those projects directly.
