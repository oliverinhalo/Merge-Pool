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

## [0.4.0] - 2026-09-25

Data format: unchanged. Config schema: unchanged — a `web` section is added with defaults, and a
0.3.0 build reads the file without migrating.

### Added

- **A web interface, on a port you choose.** Turn it on in **Settings → Web interface**, pick any
  port from 1024 to 65535, and control MergePool from a browser: pool status with both usage
  figures, per-drive throughput, mount, unmount, rebalance, add a drive, and remove one either way
  round. The service serves it, so it works whether or not the window is open.
- The port, who may reach it (this computer only, or anything on your network), whether the browser
  may change anything or only look, and whether MergePool opens the Windows Firewall port for it are
  all settings. The access token is shown, can be copied, and can be replaced — which signs out
  every browser using the old one.

### Security

- The interface is **off by default**, and bound to this computer only until that is explicitly
  changed.
- Every API request must carry the access token, generated when the interface is first turned on and
  compared in constant time. The page itself is served without one because it contains nothing but
  the sign-in shell.
- A scope the configuration does not recognise falls back to this-computer-only, so a hand-edited or
  newer config can never widen access by accident.
- The firewall rule is opened only on the private and domain profiles, only while the interface is
  both on and set to be reachable from the network, and is closed again when it is not — including
  when the service stops.
- **The connection is plain HTTP.** Anyone on the network who has the token can control MergePool,
  and the traffic is not encrypted. The window says so where the choice is made, not only here.

### Changed

- Wire protocol 4 with capability `webInterface`. `MinimumSupported` stays at 1, so an older UI is
  unaffected.

## [0.3.0] - 2026-09-25

Data format: unchanged. Config schema: unchanged — `updates` and `ui` sections are added with
defaults, and a 0.2.0 build reads the file without migrating.

### Added

- **MergePool updates itself.** The service checks the repository's releases on a timer, downloads
  the new version's package, verifies its published SHA-256, unpacks it into its own
  `versions\{version}` folder and hands off to the updater to repoint `current`. Nothing existing
  is written: the running version, every pool part and `config.json` are untouched, and a version
  that does not come up healthy is rolled back automatically. Checking and installing can each be
  turned off, and either can be run by hand from the window.
- **A drive can leave a pool**, two ways. *Remove, keep files on it* forgets the drive: its
  `.PoolPart-{GUID}` folder and every file in it stay on the drive as ordinary files. *Move files
  off, then remove* moves the pool's content onto the remaining drives first, one whole file at a
  time, and only removes the drive once it is empty — refusing up front if the others have no room,
  leaving files another program has open where they are, and keeping the drive in the pool if
  anything could not be moved.
- **Pool usage is measured, not inferred.** A pool now reports what it is holding and what it can
  hold — its own content plus the free space on its drives — separately from what those drives are
  using in total. Each drive shows the same split. The mounted volume reports the pool's ceiling
  rather than the drives' combined size, so Explorer stops counting space that other files already
  took and the pool can never use.
- **The window lives in the notification area.** Closing it hides it rather than quitting, a second
  launch raises the window that is already running, and it can open at sign-in (in the notification
  area, not in your face). The service has always started on its own and mounts the pools whether or
  not any window is open.
- **A settings tab**: how much placement leans on speed against free space, how much room to keep
  clear, how far below its own normal a drive has to fall before it is parked, and how hard the
  background rebalancer may work — each as something a person can reason about rather than a raw
  number. Plus update preferences, start-up, and a "Your files" tab that says where the data
  actually lives and what each operation does to it.
- `installer\package.ps1` and a release workflow: tagging `v0.3.0` publishes the package the
  in-app updater downloads, its checksum, and the setup executable for a first install.

### Changed

- The window was rebuilt on a real design system: tokens and control styles in one place, two
  palettes, and the window following whichever one Windows is set to.
- Wire protocol 3 with capabilities `usage`, `driveRemoval` and `autoUpdate`. `MinimumSupported`
  stays at 1, so a UI built against any earlier protocol keeps working.

## [0.2.0] - 2026-09-25

Data format: unchanged. Config schema: unchanged — a pool simply gains another drive entry, which a
0.1.0 build reads without migrating.

### Added

- Drives can be added to a pool that already exists, from the same window: select the pool, tick
  the drives, click **Add to '<pool>'**. The pool is not unmounted — its drive letter stays live,
  open files are not interrupted, and the extra space appears within a couple of seconds. Nothing
  already in the pool moves; new writes simply start landing on the new drive too. Adding a drive
  never touches what is already on it: the only thing written is its own `.PoolPart-{GUID}` folder,
  unless adoption is explicitly asked for.
- Wire protocol 2: `pool.addDrives`, announced as the `poolEdit` capability. `MinimumSupported`
  stays at 1, so a UI built against protocol 1 is unaffected, and a new UI against an old service
  feature-detects and hides the button rather than calling a method that is not there.

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
- The drive list came up empty on a real machine. A volume with no mount point — the EFI system
  partition and the recovery partition, present on every modern Windows install — makes Windows
  return just a null terminator, which was read back as the one-character path `"\0"` rather than
  as "no mount point". `DriveInfo` then threw `ArgumentException`, which was not among the caught
  types, so one hidden partition aborted the whole enumeration and every drive vanished.
  The terminator is now read correctly, `ArgumentException` and `NotSupportedException` are caught,
  and each volume is described in isolation so one unreadable volume cannot empty the list.
- The UI showed that empty list with no explanation, which reads as "this machine has no drives".
  A failed drive or pool refresh now shows the reason, and the service's underlying error message
  is carried through instead of the generic "the service failed to handle the request".
- An upgrade could stop the MergePool service and never start it again. The installer stops a
  running service before replacing its files, but starting it back up was gated on the optional
  "start the service" task checkbox, so the engine was left down and the UI came up reporting that
  the service could not be reached. An upgrade now always restarts the service it stopped, and
  waits longer for the stop to release the files.
- Upgrading swapped the `current` junction by deleting it and then moving the replacement into
  place. If that move failed, `current` was simply gone, and the service — registered at
  `{app}\current\MergePool.Service.exe` — could never start again. The new link is now built
  under a staging name first, falls back to creating the link directly if the rename fails, and
  the install verifies that `current` really resolves to `MergePool.Service.exe` afterwards.
- "The MergePool service is not running or cannot be reached" said nothing about what to do about
  it. It now names the fix, since starting a service needs an elevated shell and `Start-Service`
  from an ordinary one fails with an opaque "cannot open MergePool service" error.

- The installer failed to compile: a Pascal `{ }` comment in the `[Code]` section contained
  `{app}`, and the `}` inside it closed the comment early, leaving the rest of the sentence to be
  parsed as code. All `[Code]` comments are now `//`, which cannot be ended by a brace. CI now
  compiles the installer on Windows and uploads it, so this is caught before release.
- Dropped an installer `[Files]` entry that copied the downloaded `winfsp.msi` from `{tmp}` onto
  itself; the download already lands where `[Run]` needs it.
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
