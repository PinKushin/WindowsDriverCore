# WindowsDriverCore - COM & Exception Error Code Catalog

## Purpose
This document serves as the definitive registry for all custom and standard error codes used by the WindowsDriverCore server when communication fails or an operation cannot complete successfully.

**Structure:**
*   `[ERROR CODE]` : Short, machine-readable code (e.g., `E_UIAUTOMATION_ELEMENT_NOT_FOUND`).
*   **Description**: Plain English explanation of the failure condition.
*   **Source Module**: Which part of the driver throws this error (e.g., `ElementLocator.cs`).
*   **Handling Guide**: How the consumer/caller should handle this code gracefully, referencing guides like [007-com-exception-handling.md](GUIDES/007-com-exception-handling.md).

## Standard Error Codes (To Be Populated)
| Code | Description | Source Module |
| :--- | :--- | :--- |
| E_UNKNOWN | An unhandled error occurred on the server side. | N/A |
| ... | ... | ... |