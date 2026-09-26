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

1. Download `MergePool-0.4.1-setup.exe`.
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

#### Adding a drive to a pool later

A pool is not fixed at creation. To grow one:

1. Select the pool on the right-hand side of the window.
2. **Tick the new drive(s)** on the left.
3. Click **Add to '<pool name>'**.

The pool stays mounted the whole time. Its drive letter does not disappear, open files are not
interrupted, and the extra space shows up in Explorer within a couple of seconds. Nothing already in
the pool is moved or rewritten — new files simply start being placed on the new drive as well,
because it is the emptiest. If you want existing pooled files spread onto it, click **Rebalance**;
that runs in the background at low priority and skips files that are in use.

A drive can only be in one pool. One that already belongs to a pool shows "Already pooled" and
cannot be ticked, and a request to add it is refused rather than half-applied.

#### "I want to add a drive that has data on it I cannot afford to break"

Add it. MergePool does not reformat, repartition, move, rename or delete anything that is already on
a drive you add. All it writes to the drive is one new folder at the root, `.PoolPart-{GUID}`, and it
only ever works inside that folder. Your existing files are left exactly where they are, with the
same names, permissions and timestamps — they are simply not part of the pool, and are not visible
through the pool's drive letter. The drive keeps its own letter, and you carry on using it directly
as you always did.

Three things follow from that, worth knowing before you click:

- **Leave "Also move each drive's existing files into the pool" unticked.** That option is the only
  thing that touches existing content, and it is off by default. (Even then it is a rename within
  the same drive, never a copy or a cross-drive move, and it skips Windows and system folders — but
  if the data is irreplaceable, there is no reason to opt in.)
- **The pool's free space is the drive's free space.** MergePool does not reserve or claim the space
  your existing files occupy; the pool simply sees whatever is actually free.
- **Removing the drive later is safe.** Removing a pool leaves every file on its drive in its
  `.PoolPart-{GUID}` folder, as ordinary files, and never deletes anything.

The one precaution worth taking is the ordinary one: MergePool is not a backup, and pooling drives
does not make them redundant. If a drive dies, the files that were on it are gone — from the pool
and from the drive alike. Keep a backup of anything irreplaceable, whether or not it is pooled.

#### Taking a drive back out of a pool

Select the pool, select the drive inside it, and choose one of two buttons. Both are safe; they
differ in where the files end up.

**Remove, keep files on it.** The drive leaves the pool immediately. Nothing is deleted, nothing is
moved, nothing is copied. Its `.PoolPart-{GUID}` folder and every file in it stay on the drive as
ordinary files — they simply stop appearing in the pool. Use this when you want the drive back and
are happy for its files to go with it.

**Move files off, then remove.** MergePool first moves the pool's content from that drive onto the
pool's other drives, and only removes the drive once it is actually empty. Before anything moves it
tells you how many files and how many bytes that is, and it refuses outright if the remaining drives
do not have room. Each file is copied whole to its new drive and flushed before the original is
removed, so a file is never half-moved and never split. A file another program has open is left
exactly where it is, and if any are, the drive **stays in the pool** and MergePool says which —
close them and try again. Use this when you want the drive back but the files should stay in the
pool.

The last drive in a pool cannot be removed this way. Remove the pool instead, which also leaves
every file on its drive.

### 4. Staying up to date

**After this first install, MergePool updates itself.** The service checks for new releases a few
times a day, downloads the new version, and installs it. You do not have to do anything, and you
will not be asked to run a setup program again.

What "installs it" means here is deliberately small. The new version is unpacked into its own folder
(`versions\0.4.0` beside `versions\0.3.0`), and a single link called `current` is switched to point
at it. Nothing is overwritten. Your `config.json` lives outside the program folder entirely and is
not read or written by an update. **No pooled file is touched at any point** — an update has no
reason to open one and never does. If the new version does not come up healthy, the link is switched
straight back to the version that was working, and your pools come back with it.

The service restarts as part of this, so pools are unmounted and remounted. Anything mid-write is
flushed by the drain first; a copy in progress through Explorer may report an interruption, the same
as it would if the drive were briefly unplugged.

In **Settings → Updates and start-up** you can:

- turn off automatic checks,
- keep checks but be asked before installing,
- check or install right now, and
- see which versions are on disk. Old ones are kept so a rollback stays possible.

If you would rather do it by hand, the releases page has a setup executable as well; running it
upgrades an existing install in place.

### 5. Controlling MergePool from a browser

MergePool can serve a small web page on a port you choose, so you can check the pools or mount a
drive from a phone, a laptop, or another PC. It is **off until you turn it on**.

The quickest way in is the **MergePool** icon beside the clock: right-click it and choose **Open the
web page**. That turns the interface on if it is off, on port 8787 unless you have chosen another, and
opens it in your browser already signed in. The **Open the web page** button at the top of the
**Remote** tab does exactly the same thing.

To set it up by hand, in the **Remote** tab of the MergePool window:

1. Tick **Control MergePool from a browser**.
2. Set the **port**. Anything from 1024 to 65535; 8787 is the default. If something else is already
   using it, MergePool says so and nothing is exposed — pick another and click **Use this port**.
3. Decide who can reach it. Off (the default) means **only this computer**. Tick **Let other devices
   on my network reach it** to open it to the LAN.
4. Copy the **access token** and click **Open in browser**.

On another device, browse to `http://<this-pc-name>:<port>/` and paste the token when asked.

**How it is protected.** Every request has to present the access token; without it, the page has
nothing to show and nothing can be changed. The token is a long random string, and replacing it with
**New token** signs out every browser instantly. There is also a **look but don't touch** option that
lets a browser see the pools while refusing anything that would change them.

**What it is not.** The connection is plain HTTP — the traffic is not encrypted. Anyone on your
network who has the token can control MergePool. Use it on a network you trust; do not forward the
port to the internet.

**Firewall.** MergePool opens the port for you while the interface is on and set to reach the
network, on the private and domain profiles only, and closes it again when you turn the interface
off or stop the service. If you would rather manage that yourself, untick the option.

### 6. Where MergePool puts things

| Path | What it is |
| --- | --- |
| `C:\Program Files\MergePool\versions\0.4.1\` | The program files for one version |
| `C:\Program Files\MergePool\current` | A junction pointing at the version in use |
| `C:\ProgramData\MergePool\config.json` | Your pools and settings. Deliberately outside the program folder so updates never disturb it |
| `<each pooled drive>\.PoolPart-{GUID}\` | Your pooled files, as ordinary files in the same folder structure you see in the pool |

**Your files are plain files.** Open `D:\.PoolPart-{GUID}\Movies\` in Explorer and you will find your
movies, with normal names, permissions and timestamps. No database, no container, no proprietary
format. If MergePool is uninstalled, or the machine dies and you put the drive in another PC, the
files are still there and still readable.

### 7. Updating by hand

Run the newer installer. It stops the service, installs the new version **alongside** the old one,
and points `current` at it. Your configuration and your pooled files are untouched.

To upgrade or roll back by hand:

```powershell
# Run from an elevated PowerShell
& "C:\Program Files\MergePool\current\MergePool.Updater.exe" --version 0.3.0
```

The updater drains the pools, stops the service, swaps `current`, restarts, and health-checks the
new version. **If the new version does not come up healthy, it puts the old one back and verifies
that your pools are serving again.** Exit codes: `0` success, `1` rolled back, `3` the rollback
also failed and the machine needs attention.

### 8. Uninstalling

Uninstall MergePool from **Settings → Apps** as usual. It stops and removes the service and deletes
the program folder.

**Your pooled files are not deleted.** They stay in each drive's `.PoolPart-{GUID}` folder. To get
them back out into plain view, move the contents of that folder up to the drive root in Explorer and
then delete the empty folder.

WinFsp is left installed; remove it separately if you want it gone.

### 9. If something goes wrong

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

**MergePool's window disappeared**

Closing the window does not quit MergePool — it hides in the notification area, next to the clock,
so your pools keep being managed without a window in the way. Click its icon there to bring it back,
or use **Quit** on its menu to close it properly. Either way the service keeps the pools mounted;
the window is only a view onto it.

**An update failed**

The version that was working is still running: an update that does not come up healthy is rolled
back automatically, and the previous version's files were never touched. Settings → Updates shows
what went wrong and which versions are on disk. A failed version is not retried on every check.

If the download keeps failing, the machine may not be able to reach GitHub. Turning off automatic
checks stops MergePool trying, and you can install by hand from the releases page whenever you like.

**The window is an older version than the one that is installed**

An update adds a version directory and moves the `current` link; it cannot reach inside a window that
is already open, so a window left running keeps being the version it was started as. It now says so
across the top, with a **Reopen MergePool** button that restarts it on the installed version. Your
pools are already on the new version either way — the service restarted onto it.

If the window is opened from a path naming a version directory, such as
`C:\Program Files\MergePool\versions\0.4.0\MergePool.exe`, it hands over to the newest installed
version on start-up rather than opening the old one. Shortcuts should point at
`C:\Program Files\MergePool\current\MergePool.exe`, which always resolves to the installed version.

**Setup ended with "CreateProcess failed: code 5"**

That was setup's own "Open MergePool" tick box, not the install — everything was installed and the
service was registered. It came from an elevated installer starting a non-elevated window against the
signed-in user's token, which some machines refuse. Setup now opens the Start menu shortcut instead.
On a version that still does it, close the dialog and open MergePool from the Start menu.

**The web page will not load from another device**

Three things, in order. Is **Let other devices on my network reach it** ticked — without it only this
computer can connect. Does the **Remote** tab say **Listening**, or does it report a problem with
the port. And is the firewall letting the port through: MergePool opens it for you unless you
unticked that, but some security software adds rules of its own.

If the page loads but says the token is not accepted, copy it again from the **Remote** tab — it may
have been replaced.

**Where are the logs?**

The service logs to the Windows **Event Log**, under source `MergePool`. Open Event Viewer →
Windows Logs → Application and filter on that source.
