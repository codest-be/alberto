# Security Policy

## Supported versions

Only the most recent published version of each package receives security fixes.
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
