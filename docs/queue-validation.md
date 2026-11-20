# Durable queue validation

Validation date: 2026-09-04. Tests use generated data and fake transports; no actual microphone, camera, screen content, credentials, or Telegram traffic was used.

## Measured queue overhead

The benchmark measures its own process, with CPU expressed as a percentage of one logical core. Idle windows were 20 seconds each; these short samples do not establish long-duration power or hardware-capture performance.

| Measurement | Result |
| --- | --- |
| Baseline idle CPU | 0.156% of one core |
| Queue idle CPU | 0.078% of one core |
| Additional private memory with 512 MiB queued | 3.84 MiB |
| Control queue latency with that backlog | 3.15 ms |
| Local streaming/deletion of 512 MiB | 1.87 seconds; 0.88 CPU seconds |

The drain measurement uses local streams, not Internet upload speed. It covers queue overhead, not device drivers, Windows camera encoding, microphone helper processes, or UI responsiveness under a real network outage.

## Automated coverage

The console suite covers durable polling offsets, unauthorized payload rejection, duplicate updates, command recovery/expiry, priority capacity, reservations and quota changes, capture recovery, incomplete captures, upload recovery, delete-after-acknowledgement, FIFO retry order, recipient removal, text/media independence, atomic broadcasts, Telegram permanent errors and Retry-After, and a synthetic local Stop event.

The publication suite checks clean and unsafe staged blobs, working-tree/index differences, config-file blocking, redacted output, fail-closed inspection, and the release allowlist. Git history is checked separately without displaying potential secret values.

## Deployment checks

Build the app and NativeAOT bootstrapper, run the published app with `/queue-storage-check`, inspect the exact release ZIP entries, and enforce the existing 30 MB application / 25 MB ZIP limits.

Before deploying to a recording device, verify local approval, visible recording state, the local Stop button, camera/screenshot permission dialogs, and cancellation of native helper processes interactively. For a sustained hardware benchmark, measure the service plus helper processes during recording, disconnection, and reconnect; no claim is made that these unperformed hardware checks passed.

The store uses SQLite WAL with full synchronization. Completed media is flushed before a completion marker and durable promotion. Power loss before a capture finishes can leave an incomplete file; such a file is quarantined. A lost Telegram acknowledgement can still cause a duplicate upload. Neither case is represented as guaranteed exactly-once delivery.

The final native SQLite bundle is pinned to SQLitePCLRaw 3.0.5 (SQLite 3.53.4). The NuGet vulnerability check reported no known vulnerable direct or transitive packages. This does not replace a full security audit.
