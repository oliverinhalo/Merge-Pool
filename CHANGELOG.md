# Changelog

All notable changes to MergePool are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Two compatibility contracts are versioned separately from the app and are called out on every
change:

- **Data format** — the `.PoolPart-{GUID}` layout on disk. Pool files are plain files in mirrored
  paths, readable with MergePool uninstalled.
- **Config schema** — `config.json` `schemaVersion`, migrated forward only, backed up first,
  unknown fields preserved.

## [Unreleased]

### Added

- Core pool library: pool-part layout, volume-GUID drive identity, union view, write operations,
  placement scoring and optional adoption of existing drive content.
- Configuration store with `schemaVersion`, forward migrations, pre-migration backup and
  unknown-field preservation.
- Unit tests covering path handling, merged listings, degraded/rejoining drives, placement and
  config migration.
- Pool file system engine: open/read/write/rename/delete semantics with NTSTATUS-shaped results,
  merged directory enumeration, and whole-file relocation when a write outgrows its drive.
- WinFsp host (`MergePool.Fs.WinFsp`): WinFsp adapter, mounter, and pass-through of security
  descriptors and alternate data streams.
- Integration tests running end-to-end scenarios over fake drives.
- Placement engine: per-drive EWMA write throughput and latency from real IO plus a light idle
  probe, throttle detection against each drive's own baseline with hysteresis and cooldown, and a
  live speed factor feeding placement scores.
- Low-priority pausable rebalancer that evens out drive usage, skips files in use, respects a
  bandwidth cap and never moves the same file twice.

### Compatibility

- Data format: **1** (`.PoolPart-{GUID}` folders, mirrored paths, advisory `poolpart.json`).
- Config schema: **1**.
