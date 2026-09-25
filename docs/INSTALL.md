# Installing MergePool

MergePool is a **Windows application**. This page covers installing and running it on Windows 10
and 11. If you want to build it from source — on Windows, Linux or macOS — see
[BUILDING.md](BUILDING.md).

| Operating system | Can you run MergePool? |
| --- | --- |
| **Windows 11 (x64)** | **Yes** — fully supported |
| **Windows 10, version 1809 or newer (x64)** | **Yes** — fully supported |
| Windows on ARM | No. The installer is x64-only |
| Windows Server 2019 / 2022 | Untested. It should work; nothing in MergePool is client-only |
| Linux | No. The pool is served by WinFsp, which is Windows-only. The portable core library builds and its tests run — see [BUILDING.md](BUILDING.md) |
| macOS | No. Same as Linux |

---

## Windows

### 1. Before you start

You need three things. The installer handles the second one for you.

| What | Why | How to get it |
| --- | --- | --- |
| **Administrator rights** | MergePool registers a Windows service and writes under `Program Files` | Sign in as an administrator, or have one run setup |
| **WinFsp 2.0 or newer** | This is what presents the pool as a drive. MergePool has no kernel driver of its own | **The installer downloads and installs it if it is missing.** To install it yourself first: [winfsp.dev](https://winfsp.dev) |
| **.NET 8 Desktop Runtime (x64)** | MergePool ships as framework-dependent binaries | [dot.net/download](https://dotnet.microsoft.com/download/dotnet/8.0) → "Desktop Runtime". Pick **x64** |

> **Install the .NET 8 Desktop Runtime before running setup.** The current installer does not check
> for it, so without it the service will fail to start and MergePool's window will not open. The
> "Desktop Runtime" download includes everything both the service and the UI need — you do not need
> the SDK.

You also need at least two drives you want to pool, though MergePool will happily make a pool out of
one.

### 2. Install

1. Download `MergePool-0.1.0-setup.exe`.
2. Run it and accept the UAC prompt.
3. If WinFsp is missing, setup downloads and installs it before continuing. This needs an internet
   connection. If the download fails, setup tells you so and stops — install WinFsp yourself from
   [winfsp.dev](https://winfsp.dev) and run setup again.
4. Leave **"Start the MergePool service when setup finishes"** ticked.
5. Finish. MergePool's window opens.

Setup installs to `C:\Program Files\MergePool` unless you choose somewhere else, and registers a
service called **MergePool** (displayed as "MergePool Pool Engine") that starts automatically with
Windows.

**A restart is not required.** WinFsp may ask for one if it was installed as part of setup; you can
defer it, but pools will not mount until you have restarted.

### 3. Create your first pool

1. Open **MergePool** from the Start menu.
2. The **Drives** list shows every fixed and removable drive, with its letter, label, size and free
   space. A drive you cannot pool says why in its Status column ("Already pooled", "Not ready").
3. **Tick the drives** you want in the pool.
4. Give the pool a **name** and pick a free **drive letter** for it.
5. Leave **"Also move each drive's existing files into the pool"** unticked unless you want it — see
   the warning below.
6. Click **Create pool**.

The new drive letter appears in Explorer straight away. Copy files to it and they land on whichever
pooled drive has the best combination of free space and speed.

#### About "Also move each drive's existing files into the pool"

Unticked (the default), MergePool **does not touch anything already on your drives**. It creates one
folder — `.PoolPart-{GUID}` — at the root of each drive and only ever works inside it. Everything
else on those drives stays exactly where it is and is not visible in the pool.

Ticked, MergePool moves each drive's existing top-level files and folders *into that drive's*
`.PoolPart-{GUID}` folder, so they appear in the pool. This is a rename within the same drive, not a
copy: it is near-instant and moves no data between drives. Windows and system folders
(`Windows`, `Program Files`, `$RECYCLE.BIN`, `System Volume Information`, page files and similar)
are never adopted.

### 4. Where MergePool puts things

| Path | What it is |
| --- | --- |
| `C:\Program Files\MergePool\versions\0.1.0\` | The program files for one version |
| `C:\Program Files\MergePool\current` | A junction pointing at the version in use |
| `C:\ProgramData\MergePool\config.json` | Your pools and settings. Deliberately outside the program folder so updates never disturb it |
| `<each pooled drive>\.PoolPart-{GUID}\` | Your pooled files, as ordinary files in the same folder structure you see in the pool |

**Your files are plain files.** Open `D:\.PoolPart-{GUID}\Movies\` in Explorer and you will find your
movies, with normal names, permissions and timestamps. No database, no container, no proprietary
format. If MergePool is uninstalled, or the machine dies and you put the drive in another PC, the
files are still there and still readable.

### 5. Updating

Run the newer installer. It stops the service, installs the new version **alongside** the old one,
and points `current` at it. Your configuration and your pooled files are untouched.

To upgrade or roll back by hand:

```powershell
# Run from an elevated PowerShell
& "C:\Program Files\MergePool\current\MergePool.Updater.exe" --version 0.2.0
```

The updater drains the pools, stops the service, swaps `current`, restarts, and health-checks the
new version. **If the new version does not come up healthy, it puts the old one back and verifies
that your pools are serving again.** Exit codes: `0` success, `1` rolled back, `3` the rollback
also failed and the machine needs attention.

### 6. Uninstalling

Uninstall MergePool from **Settings → Apps** as usual. It stops and removes the service and deletes
the program folder.

**Your pooled files are not deleted.** They stay in each drive's `.PoolPart-{GUID}` folder. To get
them back out into plain view, move the contents of that folder up to the drive root in Explorer and
then delete the empty folder.

WinFsp is left installed; remove it separately if you want it gone.

### 7. If something goes wrong

**The pool drive letter does not appear**

Check the service is running: `Get-Service MergePool` in PowerShell. If it is stopped, start it with
`Start-Service MergePool`. If it will not start, the usual causes are the .NET 8 Desktop Runtime
missing, or WinFsp not installed.

**`Start-Service` says "Cannot open MergePool service on computer '.'"**

That is Windows refusing the request, not the service failing: starting a service needs
administrator rights. Close the window, open PowerShell with **Run as administrator**, and run
`Start-Service MergePool` again. Note that the error looks the same whether or not the service
itself is healthy, so an elevated retry is always the first thing to try.

**MergePool's window says "The MergePool service is not running or cannot be reached"**

The UI talks to the service over a named pipe (`MergePool.Engine`). Start the service as above. The
UI reconnects on its own once it is up — you do not need to restart the UI.

**MergePool says WinFsp is not installed**

Install it from [winfsp.dev](https://winfsp.dev) and restart the MergePool service. Pools cannot
mount without it, though your configuration is kept.

**A pool says "Degraded — 1 of 3 drives missing"**

One of the pooled drives is not connected. The pool keeps serving the files on the drives that are
present. Reconnect the drive and it rejoins on its own within a few seconds; nothing needs
repairing.

**A drive shows "Throttled"**

MergePool measured that drive writing far slower than it normally does — typically an SMR disk whose
cache is full, a USB enclosure, or a drive that is overheating. New files go to the other drives
until it recovers, which it does automatically. This is informational, not an error.

**Where are the logs?**

The service logs to the Windows **Event Log**, under source `MergePool`. Open Event Viewer →
Windows Logs → Application and filter on that source.
