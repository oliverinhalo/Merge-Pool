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

## [0.11.0] - 2026-10-23

Data format: unchanged. Config schema: unchanged — a `share` section is added to each pool and
defaults to off. IPC protocol: 8, additive.

### Added

- **MergePool shares a pool on the network itself.** Docker Desktop cannot bind-mount a WinFsp
  drive — there is no block device behind a user-mode file system for its Linux VM to recognise —
  so a container reaches the pool over SMB or not at all. Doing that by hand meant creating the
  share and then putting it back every time the pool remounted, which is a watchdog script that
  becomes the thing that breaks. The service owns the share's lifecycle instead: created when the
  pool is mounted, removed when it is not, and reconciled against the mounts on every tick, so it
  survives a service restart, an update, a drive letter change and a recovery by the I/O watchdog
  without anything having to remember it.
- **The window shows the `docker volume create` line for the share**, with a copy button. A Docker
  named volume is re-established by Docker every time a container starts, which is why it survives
  a reboot or a dropped session where a `mount -t cifs` inside the `docker-desktop` distro — a
  distro Docker rebuilds out from under you — needs a script to put it back.
- `share.get` and `share.set` over IPC, capability `networkShare`.

### Security

These are deliberate refusals, not omissions:

- **No share without a named account.** Turning sharing on is not enough: until a Windows account is
  named the share does not exist and the window says why. MergePool never grants `Everyone` and
  never guesses an account — the pool holds everything its owner has, and the person setting this up
  is the only one who knows which service account their containers use.
- **The firewall is left alone.** SMB is served by Windows on port 445 under the built-in File and
  Printer Sharing rules. Opening a system service port to a network is a bigger decision than an
  application should take on someone's behalf, so MergePool reports whether those rules are on and
  leaves them to you. (The web interface is different: it listens on a port MergePool chose, and
  opens only that one.)
- **A share MergePool did not create is never touched**, even one with the name MergePool wanted
  and even when a pool is asking for it. The pool stays unshared and says why, rather than removing
  a share nothing here knows anything about.
- Share names are reduced to what the SMB server will take, and a name with nothing usable left in
  it is refused rather than guessed at.

## [0.10.0] - 2026-10-23

Data format: unchanged. Config schema: unchanged — one optional `service.recoverStalledMounts` field
is added and defaults to the previous behaviour being impossible. IPC protocol: 7, additive.

This release is entirely about a production report: a pool backing a Docker Desktop host (Immich,
Jellyfin, Sonarr/Radarr, Kiwix reading and writing through it) that went unresponsive under
sustained concurrent I/O, transiently reported a populated folder as empty, and managed 1-2 MB/s off
four NVMe drives. All three had the same shape — work that can block, done while holding something
every other file system callback needs.

### Fixed

- **The pool could deadlock against itself and take the mount down for good.** Resolving the
  topology asks Windows to describe every volume on the machine, and one of those volumes is the
  pool's own mount. Describing it is a volume query against `P:\`, which Windows hands to WinFsp,
  which dispatches it back in as a `GetVolumeInfo` callback — on another thread, needing the same
  topology the first thread was in the middle of resolving while holding a lock. Each waited for the
  other, inside the kernel's I/O path, with nothing to time either out. That is the reported hang:
  `dir` and `vol` never returning, and processes blocked on the pool that cannot be killed even with
  a `SIGKILL`, because a thread waiting in the kernel on a file system that will never answer is not
  interruptible. Four things now make it impossible: no lock is held across a resolve; the resolve
  runs on the thread pool, never on a callback thread; volume enumeration skips our own mounts
  instead of asking us about ourselves; and a caller with nothing cached waits a bounded time and
  then gets an explicit "not ready" rather than waiting for ever.
- **Concurrent reads of one file returned each other's data.** Every read and write seeked a shared
  `FileStream` — set the position, then read — and WinFsp dispatches calls for one handle on as many
  threads as there is traffic. Two threads interleaving a seek with another's read hand back the
  wrong offset's bytes, with no error anywhere. Measured against the old code: **480 of 1600
  concurrent reads of one handle returned another offset's data, and 4 threw.** Reads and writes now
  go through `RandomAccess` by absolute offset, which is safe under concurrent use and does not
  seek. This was the worst of the three — a hang announces itself, silently wrong bytes do not.
- **Moving a file between drives mid-write could pull the stream out from under a reader.** A write
  that outgrows its drive relocates the file and swaps the handle's stream; a concurrent read was
  then using a disposed stream. Relocation, truncation, renaming and closing now take the handle
  exclusively and wait for in-flight I/O to drain, and reads and writes take it shared. Every one of
  those waits is bounded and reports a timeout rather than blocking for ever.
- **A healthy pool could report a populated folder as empty, successfully.** A drive that hiccups —
  a volume query that timed out, an enclosure that re-enumerated, a letter in flux — resolved as
  `Offline`, exactly like a drive someone had unplugged, and its files dropped out of the merged
  view. Since folders span drives and files do not, the folder still resolved on the other drives
  and listed as *empty and fine*. An application checking for a marker file reads that as deletion.
  `Directory.Exists` returning false for both "not there" and "could not ask" is the root of it.
  Now: a drive we cannot reach is `Unavailable`, not absent, for a grace period; a view missing a
  part is **incomplete**; and listings, "no such file" answers and `CanDelete` from an incomplete
  view are refused with "device not ready" instead of served as fact. A drive that is genuinely gone
  still degrades the pool exactly as before — losing a drive is supported, hiding the files on a
  drive that is merely slow to answer is not.
- **No copy of a protected folder is removed while a drive cannot be read.** The same root cause with
  teeth: "there are more copies than the rule asks for" computed from a view missing a drive is an
  undercount reasoned about as though it were complete, and acting on it deletes a copy that was not
  surplus. Surplus removal now requires a complete view.
- **Throughput.** Every write that extended a file resolved the topology — through a lock, with a
  full machine-wide volume scan every two seconds — so a 1 GB copy paid for ~16,000 serialised lock
  acquisitions interleaved with scans of every volume on the machine, including the pool itself. A
  write that fits inside the file it is already in now touches no topology at all, the scan no longer
  serialises anything, and `FileStream` buffering is gone from the data path (it was overhead on top
  of WinFsp's own caching, and a correctness hazard next to positional I/O).

### Added

- **A bounded wait everywhere the pool used to wait indefinitely**, each one reporting a timeout the
  caller can retry rather than a hang it cannot escape: 30 seconds for a read or write to get the
  handle, 60 for an operation that swaps the stream, 5 for a cold topology resolve.
- **Per-pool I/O health, and a watchdog that acts on it.** Stalls and failures are recorded per area
  of the path — reads, writes, resolving the drives — because they fail independently, and the pool's
  state is the worst of them. A pool whose I/O has stalled for 90 seconds is remounted, which is the
  one action that returns WinFsp's dispatcher state to a known good one. It is several conditions
  deep — a run of stalls rather than one slow operation, a 10-minute cooldown, never while draining,
  and switchable off with `service.recoverStalledMounts` — because a remount breaks open handles.
  Every decision is logged, including the decisions not to act. Pool data is never involved: a
  remount takes the drive letter away and brings it back, and every file stays where it is.
- **`health.check` reports whether each mount is actually answering** (protocol 7, capability
  `ioHealth`), with the stalls and failures behind the verdict. The existing check could not see this:
  it reads configuration and resolves drives, and the reply is written by a different thread from the
  ones that are stuck — which is why the service reported itself perfectly healthy throughout the
  original incident.
- **[docs/DOCKER.md](docs/DOCKER.md)** — reaching a pool from Docker Desktop and WSL2: why a bind
  mount of a WinFsp drive cannot work, the SMB path that does, and why a Docker named volume with
  CIFS options is far less fragile than a `mount -t cifs` inside the `docker-desktop` distro, which
  that distro's rebuilds take out from under you. Includes per-application notes and what MergePool
  intends to do about managing the share itself.

## [0.9.0] - 2026-10-12

Data format: unchanged. Config schema: unchanged — every setting now in the window was already a
field in `config.json`, reachable until now only by editing that file by hand. IPC protocol: 6,
additive (see [docs/UPGRADES.md](docs/UPGRADES.md)).

### Added

- **An Advanced switch in the title bar, and every tunable the engine has behind it.** The settings
  worth changing day to day — where new files go, how much space to keep clear, whether and how fast
  to even the drives out — stay in front of everyone. Turning Advanced on adds the rest: the free-space
  exponent and write headroom, the speed a parked drive has to regain, latency throttling, how many
  samples it takes to park a drive and to bring it back, the shortest time parked, the speed counted
  as normal, the three smoothing factors, the speed-factor floor and ceiling, the smallest write that
  counts, the idle probe's interval and size, the gaps that start and stop a rebalance, the largest
  file it will move, whether it gives way to real work, how copying is paced, how often protected
  folders are swept, how much room a copy leaves, how often MergePool looks for a new version, and
  whether pre-releases count. Thirty settings in all, where there were four. The switch is remembered
  per user, governs the whole window rather than one tab, changes nothing on its own, and is also
  Ctrl+Shift+A.
- **Every button, switch and slider explains itself.** Hovering anything you can act on gives a
  paragraph rather than a label: what it does, what it affects, and what it does not touch. Unmount
  says the files stay where they are; Remove pool says nothing is deleted; Rebalance says each copy is
  verified before the original goes; the drive tick box says it is a selection and not an action; the
  byte-for-byte option says what it costs. A test now fails if a control is added to the window
  without that sentence.
- **Each setting carries its own explanation on screen**, not only in a tooltip: its value in words
  ("a drive is parked once it writes below 45% of its own normal speed"), what it does, what you gain
  and give up by moving it, the value MergePool ships with, and — once it is off that value — a
  **changed** mark and a one-click way back for that setting alone. Each group of settings says how
  many of its settings are no longer at their defaults.
- **F5** refreshes the window, the same as the Refresh button.

### Changed

- The Settings tab is rebuilt around that description, with Save and Back to defaults in a footer
  that cannot be scrolled past, saying whether anything is waiting to be sent. Settings reach the
  service when you save, so a half-moved slider never does; updates and start-up are separate and
  still take effect as you set them. A periodic refresh no longer pulls a slider out from under
  whoever is moving it.
- The sliders, switches and tooltips are drawn by MergePool rather than by WPF's defaults: a filled
  track with a round thumb, a real switch for the settings that turn something on, and tooltips that
  wrap, are themed, and stay on screen long enough to read a paragraph.
- Settings that have to stay in order now do: a drive always has to regain more speed than the amount
  that parked it, and a rebalance always stops at a smaller gap than the one that starts it. Moving
  the first slider pushes the second along in front of you rather than it jumping on save.

### Fixed

- **A hand-edited `config.json` can no longer wedge the engine.** Every tunable is clamped into a
  range that still works when it is stored — a zero duplication scan interval, a zero rebalance rate,
  a speed-factor ceiling under its floor, an exit ratio under its enter ratio, a non-finite weight —
  and what the window shows after a save is what the service actually stored, so it never claims a
  value the engine is not using. Reasonable hand-edited values are left exactly as they are.

## [0.8.0] - 2026-10-10

Data format: a reserved `.staging` folder is added at the install root — see below. Config schema:
unchanged.

### Added

- **A protected folder can be changed after it is set up.** Clicking one in the list loads it into
  the panel below, so changing how many copies it keeps is the same action as setting it up in the
  first place rather than a separate mode to find; the heading and the button say which of the two
  they are doing, and **Cancel** goes back to describing a new folder. Previously the only things that
  could be done to a folder already protected were pausing it and stopping altogether.
- **The divider between the pools and the panel beside them can be dragged**, and where it is put is
  remembered for next time. Double-clicking it evens the two up again.

### Changed

- Every text style wraps now, headings included. The panels are resizable, so text that does not wrap
  is text waiting to be cut off at a width the person is entitled to choose — a long folder name in
  the protected list was the visible case.
- Clearer wording through the Protected tab: what a folder path should look like ("as the folder
  appears inside the pool, not a full Windows path"), what byte-for-byte checking costs, and what the
  surplus-copies option actually does, which now reads as **"Never delete a copy"** rather than
  describing the internal rule. Removing a folder's protection says plainly that every copy already
  on the drives is left where it is.

### Fixed

- **Closing MergePool could show "MergePool's window hit an unexpected error: The operation was
  canceled."** Shutting the window down cancels whatever it was asking the service, and the periodic
  refresh is an `async void` handler, so a refresh in flight at that moment threw into the
  dispatcher's unhandled handler and was reported as an error. It is not one: a cancelled refresh is
  a refresh whose answer has nowhere to go. The refresh, every command, and the unhandled handler
  itself now treat cancellation as what it is. 0.7.0's reopen-itself behaviour made this easy to hit,
  because handing over to a new version cancels mid-refresh by design.
- **A version the updater installed could only be read by the service that downloaded it, so the
  window would not start at all.** The download was unpacked in the service's own temp directory and
  then moved into `versions\{version}`, and moving a directory on NTFS keeps the permissions it had
  where it came from: the new parent's are never applied. For LocalSystem that means the version
  arrived readable by LocalSystem and administrators and nobody else. The window runs as whoever is
  signed in, so launching it through `current` failed with **"CreateProcess failed: code 5. Access is
  denied"** — on a copy that had updated itself, with nothing in any log to say why, and the service
  working perfectly throughout because it was the one account that could read the files. This is the
  root cause of the code 5 reported against 0.4.0, which earlier releases addressed the wrong half of.
- Downloads are now unpacked in `.staging` inside the install root, so what comes out already has the
  permissions everything else there has and the move into place needs no repair. It also keeps the
  move on one volume: a temp directory can be on another drive, and moving a directory across volumes
  is not a rename and fails outright — a second way the old path could break an update.
- The permissions of a version staged by an older updater are repaired after the move, for the
  fallback path and for anything already on disk.
- **Setup puts the whole install root back to inheriting its permissions**, which repairs every
  version an older updater left unreadable. A machine that has been auto-updating since 0.3.0 is
  fixed by installing this once.
- `.staging` is removed on uninstall, and sits beside `versions` rather than inside it: a
  half-unpacked directory there would be listed as an installed version and could be pruned
  mid-download.

## [0.7.0] - 2026-10-10

Data format: a reserved `.mergepool-work` folder is added at each pool part root, hidden from the
pool namespace — see below. Config schema: unchanged. A `duplication` section is added to each pool
and to the root with defaults, and a 0.6.0 build reads the file without migrating.

### Added

- **Protected folders: a folder inside a pool kept on more than one drive.** Point MergePool at
  `Photos`, or at the whole pool, and every file in it is kept on several drives at once. Lose a
  drive and the files are still there on the others. The copies are ordinary files in the usual
  mirrored path on each drive, so they are readable with MergePool uninstalled, like everything else
  in a pool.
- **How many copies is yours to choose.** Either a number — two survives losing one drive, three
  survives losing two at once — or a copy on *every* drive in the pool, however many that turns out
  to be. Asking for more copies than the pool has drives is not quietly rounded down: it says the
  pool needs another drive.
- **Further options per folder.** Keep copies off a drive you do not trust by naming the drives a
  folder may use. Check copies byte for byte rather than by size and date, which catches a copy that
  has rotted on the disk. And decide what happens to copies a rule no longer asks for: removed, so
  the rule is what is on the disks, or kept, so nothing is ever deleted to satisfy a rule.
- **A Protected tab**, with a line per folder saying whether it is safe yet — "all 4,182 files have
  2 copies, 41.3 GB protected", or how much is still to copy — plus **Check now**, **Pause** and
  **Stop protecting**. Stopping leaves every copy already on your drives exactly where it is.
- **The same from a browser and over the wire.** `GET`/`POST /api/pools/{id}/protected`,
  `DELETE /api/pools/{id}/protected/{ruleId}`, `POST /api/pools/{id}/protected/run`; protocol 5 adds
  `duplication.get`, `duplication.setRule`, `duplication.removeRule`, `duplication.setOptions` and
  `duplication.runNow` with capability `duplication`. `MinimumSupported` stays 1.

### Changed

- **An update now needs nothing from you.** Checking and installing were already automatic; what was
  not was the window, which stayed on the old version until it was clicked. It now reopens itself on
  the new one — straight away if it is sitting in the notification area, and after twenty seconds if
  it is in front of you, so the banner has been readable for a moment and a click does not land in a
  window that is disappearing. **Reopen this window on the new version by itself** in Settings turns
  it off for anyone who would rather be asked.

### Fixed

- **An update could delete the version a sign-in entry still named.** Versions before 0.5.0 wrote the
  window's own path into the user's Run key, so an entry can name a version directory rather than the
  version-independent one. With old versions now removed after an update, pruning could delete exactly
  what that entry pointed at, and sign-in would open nothing at all with no explanation. The updater
  reads the Run entries of every loaded user hive and keeps any version they name, until the window
  repairs its own entry the next time it starts. It only reads: quietly editing a signed-out user's
  registry is not an updater's business.
- **A rename moved only the copy the pool was reading, so the old name came back.** `PoolOperations`
  renamed the file on the drive that won the union's newest-wins comparison and left the others under
  the old name — and a listing is the union of every drive, so the old name simply reappeared. Every
  copy now follows a rename. A pool without protected folders never had more than one copy of a file,
  so this could not be hit before this release.

### Notes

- Copying is as timid as evening out drive usage: rate-capped, pausable, it gives way to real IO, and
  a file something else has open is left for the next sweep rather than copied mid-change.
- A copy is written to a hidden `.mergepool-work` folder in the part and only then moved into place.
  A half-written copy is never visible in the pool, which matters here more than anywhere: the pool
  reads the newest copy of a file, and a half-written one would be the newest. The folder is part of
  the data format and is excluded from the pool namespace, so nothing left in it by a build of any
  version is mistaken for a pooled file.
- Copies carry the original's timestamps, so a copy is never mistaken for a newer version of the file
  it is a copy of, and a folder in order is not copied again on every sweep.
- Two copies on one drive is not two copies, so a drive is never given a second copy of a file. The
  last copy of a file is never deleted, whatever a rule says.
- This is duplication, not backup: the copies live on the pool's own drives. It survives a drive
  dying. It does not survive a fire, a theft, or deleting the file yourself.

## [0.6.0] - 2026-09-26

Data format: unchanged. Config schema: unchanged.

### Added

- **"Open the web page" in the notification-area menu.** Right-click MergePool beside the clock and
  it is there, next to "Open MergePool". It turns the web interface on if it is off — on port 8787
  unless another port has been chosen — and opens it in a browser, signed in. It needs the window no
  more than the service does, which is the point of a web interface.
- **The same one-button action at the top of the Remote tab.** Previously the only way in was to tick
  a box, set a port, then find "Open in browser" further down the panel, and the open button only
  appeared once something was already listening. Now the first thing in the tab is a button that does
  the whole thing.
- **The page accepts its token from the URL fragment**, which is how MergePool hands it over: a
  fragment is never sent to the server, so the token appears in no request, log or proxy on the way
  there, and the page takes it out of the address bar as soon as it has it.

### Changed

- **Setup says when it is updating rather than installing.** It reads the version already on the
  machine and names it: the folder page, the summary before it starts and the last page all say
  "MergePool 0.5.0 is installed. This will update it to 0.6.0", that the pools, the files on the
  drives and every setting are kept, and that the previous version is removed once the new one runs.
  Reinstalling the same version says that instead.
- **The version that was replaced is removed once the new one is running.** Setup waits for the
  service to report RUNNING on the new version and only then deletes the other version directories;
  if it does not come up, every older version stays exactly where it is to fall back to. In-app
  updates do the same, keeping one version instead of two — pruning is the last step of an upgrade
  and runs only after the health check passes, so a rollback is unaffected.
- The port box now takes its range and default from the one place they are defined, rather than
  repeating 1024, 65535 and 8787 in the window.

## [0.5.0] - 2026-09-26

Data format: unchanged. Config schema: unchanged.

### Fixed

- **An update installed underneath the window, and the window stayed on the old version for ever.**
  The start-up entry was written with whatever path the window happened to have been started from. A
  window opened from a version directory — which is what anyone does when the link is broken, or when
  trying a specific build — wrote that directory into the Run key, so every sign-in afterwards opened
  that exact version however many updates had installed since. Once the version was pruned, it opened
  nothing at all. The entry is now written with the version-independent `current` path, and an entry
  already pinned by an older version is repaired the next time the window starts.
- **A window started from a version directory now hands over to the newest installed version.** It
  is the same check the start-up entry used to get wrong, made at the moment it matters: if a newer
  version is installed, the newer one is started instead and this one exits. Belt and braces for
  every stale shortcut, pinned Run entry and typed path that already exists on people's machines.
- **A window left running while an update installed now says so, with a button.** Updates add a
  version directory and move the link; they cannot reach inside a process that is already running. So
  a window that is behind the service now shows which version is installed, which one it is, and
  reopens itself on the installed one when asked. Nothing on any drive is touched.
- **Setup no longer ends with "CreateProcess failed: code 5".** Its "Open MergePool" tick box started
  the executable directly, which from an elevated installer means `CreateProcessAsUser` against the
  signed-in user's token — and on machines where that cannot be used that way it failed, turning an
  install that had worked into an error dialog. It now opens the Start menu shortcut with
  `ShellExecute`, which is what Explorer does when the shortcut is clicked.

## [0.4.1] - 2026-09-25

Data format: unchanged. Config schema: unchanged.

### Changed

- **The web interface has its own Remote tab.** It was the second-to-last section of a scrolling
  Settings tab, below the placement, throttling and rebalancing cards, which is not somewhere anyone
  finds a feature they have not been told about. It is now a tab of its own, named in the tab strip,
  with a line at the top saying what turning it on does. Nothing about the setting itself changed.

### Fixed

- **A fresh install registered the service against a path that did not exist yet.** Setup runs its
  `[Run]` entries before `ssPostInstall`, and `ssPostInstall` is where `{app}\current` is created —
  so `sc create` pointed the service at `{app}\current\MergePool.Service.exe` before that link
  existed, and the `sc start` that followed had nothing to start. Registration and starting now
  happen from `[Code]`, after the link is made. On an upgrade the link already existed, which is why
  this only ever bit a first install.
- If the link cannot be created, setup now registers the version directory directly instead of
  leaving a service pointing at nothing. MergePool works; it just cannot switch versions until the
  link is repaired, and setup says so and names the executable to open.
- The link is now verified against both executables it has to resolve, not just the service's. One
  that resolves for the service and not the window is not a working install.
- Setup's "Open MergePool" now launches the version directory directly rather than through the link,
  and as the signed-in user rather than the administrator who ran setup. The window is `asInvoker`
  by design and keeps its start-up setting in that user's own registry, so launching it elevated put
  it in the wrong place.

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
