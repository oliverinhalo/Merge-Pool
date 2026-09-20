# MergePool

A DrivePool-style drive pooling app for Windows 10/11. Selected drives are merged into one virtual
drive served by [WinFsp](https://winfsp.dev) — no custom kernel driver.

## What it does

- Pools any set of fixed or removable drives behind a single drive letter.
- Stores pooled files inside a `\.PoolPart-{GUID}\` folder on each drive, as **normal files in
  mirrored paths**. Uninstall MergePool and every file is still there, readable, where you'd expect.
- Never touches anything outside those folders. Existing drive content is left alone unless you
  explicitly adopt it (a same-volume move, never a copy).
- Never splits a file: each file lives whole on one drive; folders may span drives.
- Identifies drives by volume GUID, so drive letters can move. A missing drive means a degraded
  pool, not a broken one, and it rejoins automatically when it returns.
- Places new files on the drive with the best `free space × speed factor` score, skipping drives it
  detects as throttled (SMR cache exhaustion, USB, thermal).

## Layout

| Path | What it is |
| --- | --- |
| `src/MergePool.Core` | Pool model, union view, placement, config. Portable, no Windows deps. |
| `src/MergePool.Ipc` | Versioned named-pipe protocol, server and client. Portable. |
| `src/MergePool.Engine` | Pool engine: config, runtimes, mounting, drain/resume, health. Portable. |
| `src/MergePool.Fs.WinFsp` | WinFsp adapter and mounter. Windows only; needs WinFsp installed. |
| `src/MergePool.Service` | Windows service host. Windows only. |
| `src/MergePool.Ui` | WPF front end. Windows only. |
| `src/MergePool.Update` | Side-by-side install layout and the upgrade/rollback sequence. Portable. |
| `src/MergePool.Updater` | Upgrade CLI wiring the coordinator to the service and the junction. Windows only. |
| `installer/` | Inno Setup script and the publish + package build script. |
| `tests/MergePool.Core.Tests` | Unit tests over temp folders standing in for drives. |
| `tests/MergePool.Integration.Tests` | End-to-end scenarios over fake drives. |
| `tests/MergePool.Ipc.Tests` | Protocol, framing and engine-over-pipe tests. |
| `tests/MergePool.Update.Tests` | Upgrade, rollback and install-layout tests. |

## Building

```
dotnet build MergePool.sln            # Windows: everything
dotnet build MergePool.Portable.slnf  # any OS: the portable core and its tests
dotnet test MergePool.Portable.slnf
```

Windows-only projects (WinFsp host, service, WPF UI) are excluded from the portable solution filter
and fail fast if built on another OS.

## Compatibility promise

Updates must never break a running setup:

- The on-disk data format is stable and independent of the app version.
- The engine runs as a Windows service; the UI talks to it over a named pipe with a versioned
  protocol, so an old UI keeps working against a new service.
- Config is migrated forward only, backed up before migrating, and unknown fields are preserved.

See [CHANGELOG.md](CHANGELOG.md) and [docs/UPGRADES.md](docs/UPGRADES.md).

## Installing

`installer\build.ps1` publishes the service, UI and updater and compiles the Inno Setup installer.
Setup needs administrator rights: it installs WinFsp when missing and registers the MergePool
service. Uninstalling removes the app and leaves every pooled file exactly where it is.
