# FileMover

A single self-contained Windows executable that copies and moves files between **local disks**, **network shares (UNC)**, and **remote SSH/SFTP servers** — with parallel transfers, MD5 verification, resumable sessions, and a choice of two user interfaces from the **same binary**:

- **`-d`** → a **WPF desktop app** (native Windows window, charcoal dark theme).
- **`-w`** → an **ASP.NET Core + SignalR web app** served at `http://localhost:5050`.

Both UIs drive the identical copy engine, so behavior, options, and results are the same no matter which one you launch.

---

## Table of contents

- [What it does](#what-it-does)
- [Feature overview](#feature-overview)
- [Installation](#installation)
- [Usage](#usage)
  - [Command-line flags](#command-line-flags)
  - [Desktop UI walkthrough](#desktop-ui-walkthrough)
  - [Web UI walkthrough](#web-ui-walkthrough)
- [Copy options explained](#copy-options-explained)
- [How transfers work](#how-transfers-work)
  - [Supported transfer directions](#supported-transfer-directions)
  - [Parallelism & the SFTP connection pool](#parallelism--the-sftp-connection-pool)
  - [Verification (MD5)](#verification-md5)
  - [Permissions (chmod)](#permissions-chmod)
  - [Move semantics](#move-semantics)
  - [Disk-space checks](#disk-space-checks)
  - [Resumable sessions](#resumable-sessions)
- [Authentication](#authentication)
- [Building from source](#building-from-source)
- [Architecture](#architecture)
- [Theme & design](#theme--design)
- [Troubleshooting](#troubleshooting)
- [Limitations & notes](#limitations--notes)

---

## What it does

FileMover transfers files in any combination of three location types:

| Location | How it's reached |
|---|---|
| **Local** | Any drive letter, path, or Windows **UNC share** (`\\server\share`) |
| **SSH / SFTP** | A remote host reached over SFTP (port 22 by default) |

You pick a **source** and a **destination**, choose what to transfer (a single file, a hand-picked set of files, or the full contents of a folder), tune the options, and start. FileMover enumerates the work, copies it in parallel, optionally verifies every file with an MD5 checksum, optionally applies Unix permissions on the remote side, reports live progress (speed, ETA, per-file and overall percentages), and — if it's a **Move** — deletes the sources only after everything copied cleanly.

If a transfer is interrupted (cancel, crash, network drop), the partial progress is saved to a **session file** and can be **resumed** byte-accurately from where it stopped.

---

## Feature overview

- **All four local/SSH combinations** — Local→Local, Local→SSH, SSH→Local, and (browse-only) SSH endpoints. *(SSH→SSH copy is not supported.)*
- **Three transfer modes** — single file, multiple files, or recursive folder contents.
- **Copy or Move** — Move deletes the source(s) after a fully successful, verified copy and prunes emptied directories.
- **Parallel transfers** — 1–16 concurrent workers; remote endpoints use a per-thread SFTP connection pool.
- **MD5 verification** — every copied file is hashed on both ends and compared; mismatches are flagged as failures.
- **Resumable sessions** — interrupted transfers continue from the exact byte offset already on disk.
- **chmod after copy** — apply an octal file mode (default `644`) to files landed on an SSH destination; created directories get `755`.
- **Disk-space guard** — shows destination free space and (for local sources) warns before starting if the source is larger than the free space available.
- **Built-in file browsers** — navigate local drives, UNC servers/shares, and remote SFTP trees in a modal dialog; pick files or folders.
- **Live progress** — overall %, current-file %, transfer speed, ETA, elapsed time, file counts, and bytes moved.
- **Two UIs, one EXE** — native desktop or browser, your choice at launch.
- **Self-contained** — single ~167 MB EXE, no .NET runtime install required, no side-car files (PDB embedded, web assets embedded as resources).

---

## Installation

No installer. Download or build `FileMover.exe`, drop it anywhere, and run it from a terminal or a shortcut.

- **Requirements at runtime:** none — the .NET runtime is bundled inside the EXE.
- **Platform:** Windows (x64 or ARM64). The desktop UI is WPF (Windows-only); the web UI's browser-open helper also has macOS/Linux branches, but the shipped build targets `net10.0-windows`.

---

## Usage

### Command-line flags

```
FileMover -d                        # Desktop (WPF) UI
FileMover -w                        # Web UI — opens http://localhost:5050 in your browser
FileMover -w --no-browser           # Web UI — do not auto-open a browser
FileMover -w --urls http://*:8080   # Web UI — bind a custom address/port
```

Running with no arguments (or an unrecognized first argument) prints usage and exits:

```
FileMover — file copy / move utility
Usage: FileMover -d | -w [--no-browser] [--urls <url>]
  -d   Desktop (WPF) UI
  -w   Web UI  (browser at http://localhost:5050)
```

Notes:
- In **desktop mode** the console window is hidden automatically so only the WPF window shows.
- In **web mode** the default bind URL is `http://localhost:5050`. Override it with `--urls` (standard ASP.NET Core host syntax, e.g. `http://*:8080` to listen on all interfaces). The server prints `FileMover Web running at <url>` on startup.
- `--no-browser` is useful when running the web UI on a headless or remote machine.

### Desktop UI walkthrough

1. Launch `FileMover -d`.
2. In **OPTIONS**, choose Copy/Move, the transfer mode, and the checkboxes (verify, overwrite, recursive, set permissions), plus the concurrency slider.
3. Configure the **SOURCE** and **DESTINATION** panels:
   - For **Local**, type a path or click **Browse…** to pick one.
   - For **SSH**, fill in host/port/username, choose password or key-file auth, then click **Connect**. Browse and the path field stay disabled until the connection succeeds.
4. Watch the destination **free space** readout; if the source is bigger, you'll be warned.
5. Click **Start**. Live progress and a color-coded log update as the job runs. **Cancel** stops it (and saves a resumable session).

The window uses a custom charcoal title bar with a maximize fix so it never covers the taskbar.

### Web UI walkthrough

1. Launch `FileMover -w` — your default browser opens to `http://localhost:5050`.
2. The single-page UI mirrors the desktop layout: **OPTIONS**, **SOURCE**, **DESTINATION**, **PROGRESS**, and **LOG** panels, plus a top **Light Mode / Dark Mode** toggle.
3. The same Connect-before-Browse gate applies to SSH endpoints.
4. **Browse…** opens a draggable modal: navigate with the path bar and **Up** button, double-click to enter folders, and **Select Folder** / **Select** to choose. For local browsing on Windows it lists drives at the root and can enumerate **UNC server shares** when you type `\\server`.
5. Click **▶ Start Copy**; progress streams over SignalR in real time. **✕ Cancel** aborts.

The web server processes **one copy job at a time** — if a second client tries to start while a job is running, it receives a **Busy** signal.

---

## Copy options explained

| Option | Default | Meaning |
|---|---|---|
| **Operation** | Copy | `Copy` leaves sources intact; `Move` deletes sources after success. |
| **Mode** | Folder | `Single file`, `Multiple files` (hand-picked list), or `Folder contents`. |
| **Verify (MD5)** | On | Hash each file on both ends after copy; mismatch ⇒ the file is marked failed. |
| **Overwrite existing** | On | Off ⇒ skip files that already exist at the destination (unless resuming). |
| **Recursive** | On | For Folder mode, descend into subdirectories (preserving relative structure). |
| **Set permissions (SSH)** | On / `644` | Octal file mode applied to files written to an SSH destination; validated as octal before the job starts. Created dirs get `755`. |
| **Concurrent transfers** | 4 | 1–16 parallel workers. |

---

## How transfers work

### Supported transfer directions

| Source → Destination | Supported? |
|---|---|
| Local → Local | ✅ |
| Local → SSH | ✅ (chmod applied if enabled) |
| SSH → Local | ✅ |
| SSH → SSH | ❌ — throws `SSH-to-SSH is not supported.` |

Destination directories are created automatically as needed (locally via `Directory.CreateDirectory`, remotely by walking and `mkdir`-ing each path segment, applying the directory mode).

### Parallelism & the SFTP connection pool

A `SemaphoreSlim` caps in-flight files at the chosen thread count. Each **SSH endpoint** gets an `SftpPool` sized to that same count, so every worker borrows its **own** SFTP connection (SFTP clients are not thread-safe). Connections are reused across files and disposed when the job ends; dead connections are detected and replaced on acquire.

Transfers use a **4 MB buffer**. Local↔local copies use fully async file streams; SFTP up/downloads use SSH.NET's streaming upload/download with progress callbacks.

### Verification (MD5)

When **Verify** is on, after a file copies FileMover computes its MD5 on both ends and compares (case-insensitive):
- **Local side:** `MD5.HashDataAsync` over the file stream.
- **Remote side:** runs `md5sum '<path>'` over SSH, falling back to `md5 -q '<path>'` (BSD/macOS). Paths are shell-quoted safely.

A mismatch sets the file's error to *"Checksum mismatch after copy"* and counts it as failed.

### Permissions (chmod)

For an **SSH destination** with **Set permissions** enabled, the octal mode (e.g. `644`) is parsed and applied to each uploaded file via SFTP `ChangePermissions`. Directories created during the transfer receive `755`. An invalid (non-octal) mode parses to "no change" rather than failing the job.

### Move semantics

A **Move** runs as a normal copy first. Source deletion happens **only if zero files failed**:
- **Local source:** deletes each file, then removes directories that became empty, bottom-up.
- **SSH source:** deletes each remote file over SFTP, then removes emptied remote directories.

If anything failed (or you cancelled), sources are left untouched so nothing is lost.

### Disk-space checks

- **Destination free space** is queried and displayed:
  - Local: `DriveInfo.AvailableFreeSpace` for the path's drive.
  - SSH: `df -Pk '<path>' | awk 'NR==2 {print $4}'` (KB → bytes).
- **Source size** is measured for **local sources only** (SSH source sizing is skipped as too expensive) and used to warn you before starting if it exceeds the destination's free space.

### Resumable sessions

Every job is backed by a **session file** — a JSON manifest of all files and their status (`Pending` / `Completed` / `Failed`), written atomically (temp file → rename) so a crash never corrupts it.

- The session filename is a stable MD5 hash of the job's source+destination identity, so the *same* job always maps to the *same* session file.
- Location: `%LOCALAPPDATA%\FileMover\sessions\<hash>.json`.
- On resume, FileMover rebuilds the list of not-yet-completed files and, for each, checks the bytes already present at the destination — if the partial file is smaller than expected it continues **appending** from that exact offset; partial bytes already on disk are credited to the overall progress.
- On full success the session file is **deleted**. On cancel/failure it's **kept** so you can resume.

---

## Authentication

SSH endpoints support two auth methods:

- **Password** — username + password.
- **Private key** — path to a key file (`.pem` / `.ppk`-style); password field is replaced by the key path.

Connections are made with SSH.NET (`SSH.NET 2025.1.0`). Connect/test uses a 15–30 s timeout depending on the operation. The **Test/Connect** action opens and immediately closes an SFTP session to confirm credentials and reachability before you can browse or start.

> **Security note:** credentials are entered in the UI and sent to the engine in-process (desktop) or over the local SignalR connection (web). The web UI binds to `localhost` by default — only widen the bind address (`--urls http://*:port`) on networks you trust.

---

## Building from source

Requires the **.NET 10 SDK** (the project targets `net10.0-windows` with WPF + ASP.NET Core).

```powershell
cd <repo>\src        # the folder containing FileMover.csproj and build.ps1
.\build.ps1                       # win-x64 Release → publish\win-x64\FileMover.exe
.\build.ps1 -Runtime win-arm64    # ARM64 build
.\build.ps1 -Configuration Debug  # Debug build
```

`build.ps1` is a thin `dotnet publish` wrapper (PowerShell 5.1 compatible) that produces a **single, self-contained** EXE:

```
dotnet publish FileMover.csproj `
    --configuration Release --runtime win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    --output publish\win-x64
```

Output: one `FileMover.exe` (~167 MB). PDB is embedded; `wwwroot` is embedded as resources; `web.config` generation is suppressed; the static-web-assets pipeline is disabled.

---

## Architecture

| File | Role |
|---|---|
| `FileMover.csproj` | `Microsoft.NET.Sdk.Web`, `net10.0-windows`, WPF + ASP.NET Core, `SSH.NET`. `wwwroot` embedded as resources. |
| `Program.cs` | Entry point. `-d` spins up a WPF `Application` on an STA thread; `-w` builds an ASP.NET Core host with SignalR and an `EmbeddedFileProvider` serving `wwwroot`. |
| `Models.cs` | Core types: `EndpointSettings`, `CopyJob`, `FileEntry`, `LogEntry`, `ProgressSnapshot`, and `DiskSpaceHelper` (free-space + source-size queries). |
| `CopyEngine.cs` | The heart: enumerate files, parallel copy across all local/SSH combos, MD5 verify, chmod, progress/speed/ETA tracking, plus the `SftpPool`. |
| `CopyHub.cs` | SignalR hub for the web UI: `StartCopy`, `Cancel`, `BrowseLocal`, `BrowseSsh`, `TestSshConnection`, `GetDestFreeSpace`, `GetSourceSize`. Includes UNC share enumeration via `NetShareEnum`. |
| `CopySession.cs` | Session persistence — manifest model + atomic save/load/delete keyed by job identity. |
| `MainWindow.xaml(.cs)` | WPF desktop UI — charcoal theme, custom title bar, `WM_GETMINMAXINFO` maximize fix. |
| `SftpBrowserWindow.xaml(.cs)` | Desktop SFTP folder-browser dialog. |
| `wwwroot/index.html` | Web UI shell (panels for options, source, destination, progress, log). |
| `wwwroot/app.js` | SignalR client, browse modal, drag handling, all web UI logic. |
| `wwwroot/styles.css` | Dark/light theme via CSS custom properties (`data-theme` toggle). |
| `build.ps1` | `dotnet publish` wrapper. |

**Progress model:** the engine raises `ProgressChanged` (throttled by a 400 ms timer) and `LogMessage` events. The desktop UI binds to them directly; the web hub forwards them to the browser as SignalR `Progress` / `Log` / `StateChanged` messages. Speed is computed from a rolling window of byte/time samples; ETA is derived from current speed and remaining bytes.

---

## Theme & design

Both UIs share a **charcoal dark** palette with a **steel-blue** accent (no purple):

| Token | Value |
|---|---|
| Background | `#1A1A1A` |
| Surface | `#252525` |
| Border | `#3D3D3D` |
| Text | `#E8E8E8` |
| Accent | `#5B9BD5` (steel blue) |
| Success | `#5DB85D` |
| Warning | `#E8A838` |
| Error | `#E05050` |

The web UI implements this with CSS custom properties and a `data-theme` attribute (dark/light toggle in the title bar). The desktop UI uses the WPF `Resources["BrushKey"]` pattern.

---

## Troubleshooting

| Symptom | Likely cause / fix |
|---|---|
| **Browse / path disabled for SSH** | You must click **Connect** first; credential changes reset the auth gate and clear the free-space display. |
| **"Busy"** in the web UI | A copy is already running; the web server handles one job at a time. Wait for it to finish or cancel it. |
| **SSH→SSH fails** | Not supported — stage through a local path (SSH→Local, then Local→SSH). |
| **Verify fails on a remote** | The remote needs `md5sum` (Linux) or `md5` (BSD/macOS) on its `PATH`. |
| **Free space shows `—` for SSH** | The remote needs `df` and `awk`; the path must exist and be reachable. |
| **Interrupted transfer** | Re-run the same job to resume — partial files continue from their existing byte offset. |
| **Can't see UNC shares** | Type `\\server` (Windows only) to enumerate shares; requires access rights to that server. |

---

## Limitations & notes

- **SSH→SSH** direct transfers are intentionally unsupported.
- **SSH source size** isn't measured, so the pre-copy "source larger than free space" warning only fires for **local** sources.
- The **web UI runs a single job at a time** process-wide.
- The desktop UI is **Windows-only** (WPF); the published build targets `net10.0-windows`.
- Sessions live under `%LOCALAPPDATA%\FileMover\sessions\`; deleting them simply means a future identical job starts fresh instead of resuming.
- `df` free-space query: `df -Pk '<path>' | awk 'NR==2 {print $4}'` returns KB and is multiplied by 1024.
