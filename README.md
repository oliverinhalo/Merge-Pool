# MergePool

A DrivePool-style drive pooling app for Windows 10 and 11. Pick some drives, pick a drive letter,
and they appear as one. The virtual drive is served by [WinFsp](https://winfsp.dev) — there is no
custom kernel driver.

## Getting started

| I want to… | Go to |
| --- | --- |
| **Install and use MergePool on Windows** | **[docs/INSTALL.md](docs/INSTALL.md)** |
| Build it from source (Windows, Linux or macOS) | [docs/BUILDING.md](docs/BUILDING.md) |
| Understand how updates avoid breaking a running setup | [docs/UPGRADES.md](docs/UPGRADES.md) |
| See what changed | [CHANGELOG.md](CHANGELOG.md) |

### Quick start (Windows)

1. Install the [.NET 8 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/8.0).
2. Run `MergePool-0.4.0-setup.exe` as an administrator. It installs WinFsp if you do not have it.
3. Open MergePool, tick the drives you want, choose a drive letter, click **Create pool**.

That is the only install you have to do by hand: MergePool keeps itself up to date from then on.

Full detail, including what to expect and what to do when something goes wrong, is in
[docs/INSTALL.md](docs/INSTALL.md).

## What it does

- **Pools any set of fixed or removable drives** behind a single drive letter.
- **Your files stay ordinary files.** Pooled data lives in a `\.PoolPart-{GUID}\` folder on each
  drive, in the same folder structure you see in the pool, with normal names, permissions and
  timestamps. Uninstall MergePool, or move a drive to another PC, and everything is still there and
  still readable.
- **Leaves your existing data alone.** MergePool only ever writes inside its own `.PoolPart-{GUID}`
  folders. Adopting a drive's existing content into the pool is opt-in, and it is a move within the
  same drive — never a copy between drives.
- **Grows and shrinks without downtime.** Add a drive to a running pool and the extra space shows up
  in seconds, with nothing already in it moved. Take one back out either by leaving its files on it
  or by moving them onto the remaining drives first — MergePool tells you what would move, refuses
  if it will not fit, and keeps the drive in the pool if anything could not be moved.
- **Says what the pool is holding**, as opposed to what its drives are holding. A pooled drive can be
  full of files that were never pooled; the two figures are measured and shown separately, and the
  pool's ceiling is its own content plus the space actually free.
- **Controllable from a browser**, on a port you choose, if you want it: the same pool status and
  controls, served by the service on your own network. Off by default, access-token protected, and
  bound to the local machine until you say otherwise.
- **Updates itself.** After the first install, new versions are downloaded, checksum-verified,
  unpacked beside the running one and switched to. Nothing is overwritten, your configuration and
  pooled files are never touched, and a version that does not come up healthy is rolled back.
- **Never splits a file.** Each file lives whole on one drive; folders may span drives. Renames and
  moves stay on the drive the file is already on, so they are instant.
- **Survives a drive going away.** Drives are identified by volume GUID, so letters can move. A
  missing drive means a degraded pool, not a broken one, and it rejoins automatically when it
  returns.
- **Puts new files where they belong.** Placement scores each drive on free space × speed, and skips
  drives it detects as throttled — an SMR disk whose cache is exhausted, a slow USB enclosure, a
  drive that is overheating — until they recover.
- **Evens itself out in the background.** A low-priority, pausable rebalancer moves files off the
  fullest drives, skipping anything in use and capping its own bandwidth.

## Updates never break a running setup

This is a design constraint, not an aspiration, and it shapes the architecture:

- The **on-disk format is stable** and independent of the app version.
- The **engine runs as a Windows service**; the UI is just a client. They talk over a named pipe with
  a **versioned protocol**, so an old UI keeps working against a new service.
- **Config is migrated forward only**, backed up before migrating, with unknown fields preserved.
- Versions install **side by side** with an atomic `current` swap. An upgrade is drain → stop → swap
  → start → health check, and **rolls back automatically** if the new version does not come up
  healthy.

See [docs/UPGRADES.md](docs/UPGRADES.md).

## Repository layout

| Path | What it is | Builds on |
| --- | --- | --- |
| `src/MergePool.Core` | Pool model, union view, placement, throttling, config | any OS |
| `src/MergePool.Ipc` | Versioned named-pipe protocol, server and client | any OS |
| `src/MergePool.Engine` | Pool engine: config, runtimes, mounting, drain/resume, health | any OS |
| `src/MergePool.Update` | Side-by-side install layout, release feed, upgrade and rollback | any OS |
| `src/MergePool.Fs.WinFsp` | WinFsp adapter and mounter | Windows |
| `src/MergePool.Service` | Windows service host | Windows |
| `src/MergePool.Ui` | WPF front end | Windows |
| `src/MergePool.Updater` | Upgrade CLI | Windows |
| `src/MergePool.Web` | Optional web interface: HTTP server, JSON API, single-page app | any OS |
| `tests/` | 323 tests, all runnable on any OS | any OS |
| `installer/` | Inno Setup script and the publish + package script | Windows |

## Status

Version 0.4.0. All six milestones are in — core pool library, WinFsp mount, placement and throttle
engine, service and IPC, WPF UI, installer and updater — plus growing and shrinking a live pool,
measured pool usage, and updating itself from the release feed.

323 tests pass on Linux and Windows. CI compiles the full Windows solution against a real WinFsp
install and builds the installer, so a `[Code]` change that will not compile fails there rather than
on a user's machine.

The installer, the service and the UI have now been run on a physical Windows machine: the service
installs and starts, the UI connects to it and lists the drives. Mounting a pool with WinFsp and the
placement engine under real IO have not yet been through the same scrutiny, so treat those as
verified by tests and not by use.
