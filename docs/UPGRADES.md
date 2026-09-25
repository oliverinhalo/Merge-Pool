# Upgrades

An update must never break a running setup. That constraint drives every design decision below.

## What an upgrade is allowed to change

| Thing | Changed by an upgrade? |
| --- | --- |
| Pooled files on the drives (`.PoolPart-{GUID}\…`) | Never |
| `.PoolPart-{GUID}` layout | Never — the data format is stable and version independent |
| `%ProgramData%\MergePool\config.json` | Only by a forward migration, after a backup |
| `%ProgramFiles%\MergePool\versions\{version}\` | Added; existing version directories are never modified |
| `%ProgramFiles%\MergePool\current` | Repointed at the new version — this *is* the upgrade |

## Side-by-side install

```
%ProgramFiles%\MergePool\
  versions\
    0.1.0\        <- installed, untouched after install
    0.2.0\        <- installed by the new setup
  current  ->  versions\0.2.0     (directory junction)
%ProgramData%\MergePool\
  config.json                     (never inside the install root)
```

The service is registered with `{app}\current\MergePool.Service.exe`. Because Windows resolves that
path when the process starts, repointing the junction while the service is stopped is the entire
swap, and it is effectively atomic: the new link is built beside the old one and moved into place,
so a crash part-way leaves either the old link or the new one, never a missing `current`.

## The upgrade sequence

`MergePool.Updater --version <version>` runs `UpgradeCoordinator`:

1. **Drain** — ask the running service to unmount every pool (`upgrade.drain` over the pipe), so
   nothing is mid-write. A service that is already down is not an error.
2. **Stop** the Windows service, releasing the version directory it was running from.
3. **Swap** `current` to the new version directory.
4. **Start** the service.
5. **Health check** — reconnect, resume the pools, and ask for a health report, retrying while the
   new service is still starting.
6. **Prune** old version directories, always keeping the active one and the rollback target.

If step 4 or 5 fails, the coordinator **rolls back**: stop, point `current` back at the previous
version, start, and verify that the pools came back. The previous version's files were never
touched, so there is nothing to restore.

Outcomes the updater reports:

| Outcome | Meaning | Exit code |
| --- | --- | --- |
| `Succeeded` | The new version is running and healthy | 0 |
| `AlreadyCurrent` | Nothing to do | 0 |
| `RolledBack` | The new version failed; the previous one is serving again | 1 |
| `Failed` | The upgrade failed *and* the rollback failed — needs a human | 3 |

## Protocol compatibility

The UI and the service talk over a named pipe with a versioned protocol
(`MergePool.Ipc.ProtocolVersion`):

- The service accepts every version from `MinimumSupported` to `Current`, so an **old UI keeps
  working against a new service**.
- A version is bumped only for additive changes. Removing or re-shaping anything raises
  `MinimumSupported`, which is a major release.
- Both sides preserve fields they do not recognize, so a newer service can send more than an older
  UI expects.
- New functionality is announced through capabilities, not inferred from the version number, so a
  UI feature-detects rather than guessing. An unknown method comes back as a structured
  `method_not_supported` error, never a dropped connection.

### Protocol history

| Version | Added | Older clients |
| --- | --- | --- |
| 1 | The original method set | — |
| 2 | `pool.addDrives`, capability `poolEdit` | Unaffected: `MinimumSupported` is still 1, and a v1 UI never calls the new method |
| 3 | `pool.removeDrive`, `pool.planDriveRemoval`, `update.*`, measured usage fields, capabilities `usage`, `driveRemoval`, `autoUpdate` | Unaffected: the new fields are additive and an older UI never calls the new methods |
| 4 | `web.get`, `web.set`, `web.regenerateToken`, capability `webInterface` | Unaffected: an older UI never calls them, and the web interface is off until turned on |

## Config migrations

`ConfigStore` migrates forward only, backs the file up before migrating, preserves unknown fields,
and writes atomically. A config written by a newer build keeps its own `schemaVersion` when read by
an older one, so downgrading does not silently rewrite it.
