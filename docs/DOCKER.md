# Reaching a pool from Docker Desktop and WSL2

This page is for the setup MergePool is most often asked to back: a Windows machine running Docker
Desktop with the WSL2 backend, where containers — Immich, Jellyfin, Sonarr, Radarr, Kiwix — need to
read and write the pool.

It is written from a production report rather than from theory, including the parts that are
awkward.

---

## Why a bind mount does not work

```
docker run -v P:\media:/media ...
→ error: volume does not contain a recognized file system
```

This is expected, and it is not something MergePool can fix from its side.

Docker Desktop's WSL2 backend does not see `P:` as a path on the host. It sees the host's **real
volumes**, surfaced into the Linux VM by the Docker Desktop host service, and it identifies them by
asking the volume what file system it is. A MergePool pool is a virtual volume served by WinFsp in
user mode: it exists for Windows processes that go through the Windows I/O manager, and there is no
block device behind it for the VM to recognise. The same is true of every user-mode file system on
Windows — Dokan drives, rclone mounts, Google Drive's virtual drive — and of DrivePool, for the same
reason.

So the pool has to be reached over a protocol rather than as a path. On Windows that means SMB.

---

## The supported path: SMB, mounted per container

Sharing the pool over SMB and mounting it inside the container works, and is what MergePool
recommends today. The part worth getting right is **how** the container mounts it, because that is
what decides whether it survives a reboot, a service restart or a dropped session.

### 1. Let MergePool share the pool

In the window: **Remote → Share this pool on the network**. Tick it, name at least one Windows
account that may connect, and the service does the rest.

MergePool owns the share's lifecycle: it is created when the pool is mounted and removed when it is
not, and each background tick makes the machine's shares match the pools again. That is what makes
it survive the cases a hand-written watchdog trips over — a service restart, an update, a pool
remounted by the I/O watchdog, a drive letter that changed. Nothing has to remember anything,
because each pass simply asks what should be shared and what is.

Notes that matter:

- **At least one account, always.** MergePool will not grant `Everyone` and will not guess an
  account for you. Until one is named the switch is on and the share does not exist, and the window
  says so rather than failing silently.
- **Use a dedicated local account**, not your sign-in account: containers will hold its password in
  a file. Create it in Windows first — MergePool does not create accounts.
- **MergePool does not touch your firewall for this.** SMB is served by Windows on port 445 under
  the built-in File and Printer Sharing rules, and quietly opening a system service port to a
  network is a bigger decision than an application should take on someone's behalf. If those rules
  are off, the window says so and leaves it to you. (It is different for the web interface, which
  listens on a port MergePool chose and opens only that one.)
- **Shares others own are never touched.** A share MergePool did not create, pointing somewhere
  that is not one of its mount points, is left alone even if it has the name MergePool wanted — in
  which case the pool stays unshared and says why.

Doing it by hand still works, if you would rather:

```powershell
# A stand-in: use a dedicated local account, not your own.
New-SmbShare -Name Pool -Path P:\ -FullAccess "MACHINE\poolsvc"
```

The pool must be mounted by the **MergePool service**, which it is by default. A pool mounted by a
process in your own session is not visible to the SMB server, which runs as `LocalSystem`.

### 2. Mount it as a Docker volume, not inside the WSL distro

This is the important one, and it is the fix for the fragility in the report that prompted this
page.

A `mount -t cifs` inside the `docker-desktop` distro does not survive: that distro is managed by
Docker Desktop and is rebuilt out from under you on restart and on update. A mount placed there
needs a watchdog script to put it back, and the watchdog is then the thing that breaks.

Let Docker own the mount instead. A named volume with the `local` driver and CIFS options is
established **when a container starts**, by Docker, every time:

```bash
docker volume create pool-media \
  --driver local \
  --opt type=cifs \
  --opt device=//host.docker.internal/Pool/media \
  --opt o=addr=host.docker.internal,username=poolsvc,password=…,vers=3.1.1,uid=1000,gid=1000,file_mode=0664,dir_mode=0775,nofail,soft
```

or in Compose:

```yaml
volumes:
  pool-media:
    driver: local
    driver_opts:
      type: cifs
      device: //host.docker.internal/Pool/media
      o: addr=host.docker.internal,username=poolsvc,password=${POOL_PASSWORD},vers=3.1.1,uid=1000,gid=1000,file_mode=0664,dir_mode=0775,nofail,soft

services:
  jellyfin:
    image: jellyfin/jellyfin
    volumes:
      - pool-media:/media
```

Why this is less fragile than a persistent CIFS mount:

| | CIFS mount in `docker-desktop` | Docker named volume |
| --- | --- | --- |
| Survives Docker Desktop restart | No — needs a script | Yes, re-established per container |
| Survives a dropped SMB session | No — stays broken | Recovered by restarting the container |
| Survives the pool being remounted | No | Recovered by restarting the container |
| Needs a watchdog | Yes | No |

A dropped session still interrupts a running container — SMB is not magic — but recovery is
`docker restart`, or a container restart policy, instead of a script you maintain.

### 3. Options worth setting, and why

| Option | Why |
| --- | --- |
| `vers=3.1.1` | SMB 3.1.1 is encrypted and far better behaved than the 2.x default negotiation. Drop to `3.0` only if the share refuses it. |
| `nofail` | A container must not fail to start because the share was briefly away. |
| `soft` | An I/O error is returned to the application instead of blocking it for ever. On a hard mount, a dropped share leaves processes in an uninterruptible wait — the same failure mode this release removed from the pool itself, so do not reintroduce it one layer up. |
| `uid` / `gid` / `file_mode` / `dir_mode` | CIFS cannot map Windows ACLs onto Linux users, so every file appears owned by whoever you say here. Match the uid your container runs as. |
| `actimeo=30` (optional) | Caches metadata for 30 seconds. Helps a media scanner enormously; set it to `1` if two containers write the same tree and must see each other promptly. |
| `cache=loose` (default) | Fine for one writer. With several writers across containers, prefer `cache=strict`. |

### Per-application notes

- **Jellyfin, Kiwix** — read-mostly, large sequential reads. These are the easy case.
- **Sonarr / Radarr** — their imports are a move. A move *within one share* is a rename and is
  instant; a move *between* a share and a container-local path is a copy. Keep the download folder
  and the library on the same share if you can.
- **Immich** — the one to be careful with. Its library wants POSIX semantics that CIFS approximates:
  hardlinks are unavailable, and `fsync` behaviour differs. Immich's own documentation advises
  against network storage for its database, and that advice stands here — keep Postgres on a local
  Docker volume and put only the **upload/library** tree on the pool. This was reported working in
  production, but it is the configuration most likely to surprise you.

---

## When things drop

The share's path stops resolving whenever the pool is not mounted. That happens when:

- the service restarts, including during an **update** — brief, and the pool comes straight back;
- someone unmounts the pool from the window;
- the **I/O watchdog** recovers a pool that had stopped answering — see
  [the changelog](../CHANGELOG.md). A remount is deliberate: it is how a wedged mount is recovered.

In all three cases SMB sessions against the share break, because for a moment there is nothing
behind it. A share MergePool manages is removed and recreated with the mount, so it is correct
again as soon as the pool is back; containers using a Docker named volume pick it up on restart. A
hand-made CIFS mount does not.

If you see the share itself disappear rather than just break, check whether the share was created
against the pool's drive letter while the pool was mounted *by a user session* rather than by the
service — `Get-SmbShare Pool | Select Path` and confirm the pool is mounted by the MergePool
service.

---

## What MergePool does for you

- A per-pool **"Share this pool on the network"** setting: share name, who may connect, read-only or
  read-write.
- The share created when the pool mounts and removed when it unmounts, reconciled on every tick, so
  its life is tied to the mount's by construction rather than by a script.
- The share re-established by itself after a service restart, an update, or a watchdog recovery.
- The exact `docker volume create` line for the share, shown in the window with a copy button —
  paste it, put the account's password in it, and run it.

What it deliberately does not do: grant `Everyone`, invent an account, open your firewall, or touch
a share it did not create.

---

## What will not work, and why

- **Bind-mounting the pool's drive letter.** See the top of this page.
- **Bind-mounting a `.PoolPart-{GUID}` folder on a real drive, read-write.** It will mount — those
  are plain NTFS folders on real volumes, which Docker Desktop handles fine — and it is a reasonable
  **read-only** escape hatch for one drive's share of the pool. Writing into it bypasses placement
  and duplication entirely: the file lands on a drive MergePool did not choose, the same pool path
  can end up on two drives at once, and protected folders will not know to copy it. Do not write
  into a pool part from outside MergePool.
- **NFS instead of SMB.** Windows' NFS server does not serve user-mode file systems, and the
  client-side half (`Services for NFS`) is not the part you need.
