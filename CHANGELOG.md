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

## [0.1.0] - 2026-09-20

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
- `MergePool.Ipc`: versioned named-pipe protocol with a handshake, capability negotiation,
  length-prefixed JSON framing and structured errors.
- `MergePool.Engine`: the pool engine the service hosts — configuration, per-pool runtimes,
  mounting, drain/resume for upgrades and a health check.
- `MergePool.Service`: Windows service host wiring the engine to WinFsp and the named pipe, with
  an ACL that lets the signed-in user's UI connect.
- `MergePool.Ui`: WPF front end listing fixed and removable drives with letter, label, size and
  free space, tick-to-pool selection, mount letter picker, and live per-drive pool status
  including throttling and measured throughput. Reconnects on its own when the service restarts.
- `MergePool.Update` / `MergePool.Updater`: side-by-side versioned install layout with an atomic
  `current` swap, and an upgrade sequence of drain, stop, swap, start, health check, with
  automatic rollback to the previous version when the new one does not come up healthy.
- Inno Setup installer: requires administrator, checks for WinFsp and installs it when missing,
  registers the service against `current`, and leaves every pooled file in place on uninstall.

### Fixed

- The WPF window died on launch with "Cannot find non-neutral culture related to 'en-us'."
  `InvariantGlobalization` was enabled for every project, and WPF needs real culture data to
  resolve the UI language. Removed, with a test that fails if it is ever set again — CI compiles
  the UI but never launches it, so nothing else would catch it.
- `installer\build.ps1` only looked for Inno Setup in two fixed folders and failed on a perfectly
  good install elsewhere. It now checks `PATH`, the uninstall registry entry (per-machine and
  per-user) and the usual folders including `%LOCALAPPDATA%\Programs`, takes an explicit
  `-InnoSetupPath`, and its error says the binaries are already published so the `.iss` can be
  compiled by hand.

### Documentation

- `docs/INSTALL.md`: Windows install and first-run guide — prerequisites, what the installer does,
  creating a pool, where files live, updating, uninstalling and troubleshooting, plus a table of
  which operating systems can run MergePool at all.
- `docs/BUILDING.md`: build instructions separated per operating system. Windows builds everything
  including the installer; Linux and macOS build and test the portable core.
- README rewritten as a front door that routes to the right guide.

### Compatibility

- Data format: **1** (`.PoolPart-{GUID}` folders, mirrored paths, advisory `poolpart.json`).
- Config schema: **1**.
- IPC protocol: **1** (minimum supported **1**). The service accepts every version from the
  minimum to the current one, so an old UI keeps working against a new service.
