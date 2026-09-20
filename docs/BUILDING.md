# Building MergePool from source

MergePool is split so that most of it is portable. The pool logic, the placement engine, the
IPC protocol and the upgrade logic are plain `net8.0` libraries that build and test on **any**
operating system. Only the parts that must touch Windows — the WinFsp mount, the service, the WPF
UI and the updater — are Windows-only.

That means you get the full test suite on Linux or macOS, but you can only produce a *working
MergePool* on Windows.

| | Windows | Linux | macOS |
| --- | --- | --- | --- |
| Portable core (`MergePool.Portable.slnf`) | ✅ | ✅ | ✅ |
| 208 tests | ✅ | ✅ | ✅ |
| WinFsp host, service, WPF UI, updater | ✅ | ❌ | ❌ |
| Installer | ✅ | ❌ | ❌ |

The Windows-only projects are excluded from `MergePool.Portable.slnf` and **fail the build with a
clear error** if you try to build them on another OS, rather than failing confusingly deep in the
compiler.

---

## Windows

This is the only platform that produces a complete, runnable MergePool.

### Prerequisites

| What | Version | Notes |
| --- | --- | --- |
| [.NET SDK](https://dotnet.microsoft.com/download/dotnet/8.0) | 8.0 or newer | `global.json` pins the 8.0 band and rolls forward to the newest 8.0.x you have installed |
| [WinFsp](https://winfsp.dev) | 2.0 or newer | Required **to build**, not just to run. `MergePool.Fs.WinFsp` references `winfsp-msil.dll` from the WinFsp install directory, because WinFsp does not publish it on NuGet |
| [Inno Setup](https://jrsoftware.org/isdl.php) | 6.x | Only needed to build the installer |
| Visual Studio 2022 or Rider | any recent | Optional. Everything below works from the command line |

### Build and test

```powershell
git clone https://github.com/oliverinhalo/Merge-Pool.git
cd Merge-Pool

# Everything, including the Windows-only projects
dotnet build MergePool.sln -c Release
dotnet test  MergePool.sln -c Release
```

If the build stops with *"WinFsp was not found"*, either install WinFsp or point the build at it:

```powershell
dotnet build MergePool.sln -c Release -p:WinFspBinDir="C:\Program Files (x86)\WinFsp\bin\"
```

### Build the installer

```powershell
.\installer\build.ps1 -Configuration Release
```

This publishes the service, the UI and the updater into `artifacts\publish\`, then compiles
`installer\MergePool.iss` with Inno Setup. The result is:

```
installer\Output\MergePool-0.1.0-setup.exe
```

The version comes from `<VersionPrefix>` in `Directory.Build.props`, so the installer and the
side-by-side install directory always agree. Override it with `-Version 0.2.0` if you need to.

### Run it without installing

Useful while developing, so you do not have to reinstall on every change. Run **elevated** — mounting
needs it.

```powershell
# Terminal 1: the pool engine, as a console app rather than a service,
# with its own config file so your real setup is untouched
dotnet run --project src\MergePool.Service -- --config .\dev-config.json

# Terminal 2: the UI
dotnet run --project src\MergePool.Ui
```

The UI finds the engine over the `MergePool.Engine` named pipe either way, so it does not care
whether the engine is a service or a console process.

---

## Linux

You can build and test the portable core. You cannot produce a runnable MergePool — there is no
WinFsp, no Windows service and no WPF.

This is a perfectly good way to work on the pool logic, placement and throttling, the IPC protocol
or the upgrade sequence, all of which are covered by the test suite.

### Prerequisites

Just the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
# Debian / Ubuntu
sudo apt-get update && sudo apt-get install -y dotnet-sdk-8.0

# Fedora
sudo dnf install dotnet-sdk-8.0

# Arch
sudo pacman -S dotnet-sdk

# Any distribution, no root needed
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0
export PATH="$PATH:$HOME/.dotnet"
```

### Build and test

```bash
git clone https://github.com/oliverinhalo/Merge-Pool.git
cd Merge-Pool

dotnet build MergePool.Portable.slnf -c Release
dotnet test  MergePool.Portable.slnf -c Release
```

Use `MergePool.Portable.slnf`, **not** `MergePool.sln` — the full solution includes the Windows-only
projects and will stop with an error telling you exactly this.

The tests stand temp directories in for drives, so they exercise the same code paths a real pool
does: merged listings, a drive disappearing and rejoining, whole-file moves when a write outgrows a
drive, config migration, the engine behind a real named pipe, and upgrade rollback.

---

## macOS

Identical to Linux: the portable core builds and its tests pass; nothing Windows-specific does.

### Prerequisites

The [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) — the installer `.pkg`, or:

```bash
brew install --cask dotnet-sdk
```

Both Apple Silicon and Intel work.

### Build and test

```bash
git clone https://github.com/oliverinhalo/Merge-Pool.git
cd Merge-Pool

dotnet build MergePool.Portable.slnf -c Release
dotnet test  MergePool.Portable.slnf -c Release
```

---

## Project layout

| Project | Target | Builds on |
| --- | --- | --- |
| `src/MergePool.Core` | `net8.0` | any | 
| `src/MergePool.Ipc` | `net8.0` | any |
| `src/MergePool.Engine` | `net8.0` | any |
| `src/MergePool.Update` | `net8.0` | any |
| `src/MergePool.Fs.WinFsp` | `net8.0-windows` | Windows + WinFsp |
| `src/MergePool.Service` | `net8.0-windows` | Windows |
| `src/MergePool.Ui` | `net8.0-windows` (WPF) | Windows |
| `src/MergePool.Updater` | `net8.0-windows` | Windows |

Tests live beside them in `tests/`, all `net8.0` and all runnable anywhere.

## CI

`.github/workflows/ci.yml` runs three jobs on every push and pull request:

- **Portable core (ubuntu-latest)** — builds and tests `MergePool.Portable.slnf`
- **Portable core (windows-latest)** — the same, on Windows
- **Full solution (windows)** — installs WinFsp with Chocolatey, then builds and tests
  `MergePool.sln`, which is what compiler-checks the WinFsp host, the service, the WPF UI and the
  updater

If you are developing on Linux or macOS, that third job is what catches Windows-only mistakes, so
watch it on your pull request.
