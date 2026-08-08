# WindowsDriverCore - API Versioning and Compatibility Policy

## Purpose
This document defines the process and rules for managing changes to the public API of WindowsDriverCore (e.g., changes to request/response structures, new required parameters). Consistency with Appium and W3C standards is paramount.

**Policy Pillars:**
1.  **Backward Compatibility First:** All non-breaking additions are preferred. Existing clients should continue functioning without code changes after an update.
2.  **Breaking Changes:** Must be treated as a major version bump (e.g., moving from v1 to v2). A detailed migration guide must accompany the release.
3.  **Deprecation Cycle:** No endpoint or parameter should be removed without being marked `[DEPRECATED]` in this document and providing clear guidance on its replacement, giving at least two major versions notice.

## Versioning Schema (Example)
*   `MAJOR`: Reserved for breaking API changes.
*   `MINOR`: Reserved for adding new functionality/endpoints that do not break existing calls.
*   `PATCH`: For bug fixes or non-functional updates only.

**Referenced Documents:** [technical-design.md](ARCHITECTURE/technical-design.md) dictates the current scope, but this policy governs *how* we expand it.