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
- [Security](#security)
- [Building from source](#building-from-source)
- [Testing](#testing)
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
FileMover -w --token <16+ chars>    # Web UI — use a fixed access token instead of a random one
```

Running with no arguments (or an unrecognized first argument) prints usage and exits:

```
FileMover — file copy / move utility
Usage: FileMover -d | -w [--no-browser] [--urls <url>] [--token <value>]
  -d   Desktop (WPF) UI
  -w   Web UI  (browser at http://localhost:5050)
       --token  fixed access token (min 16 chars); default is random per launch
```

Notes:
- In **desktop mode** the console window is hidden automatically so only the WPF window shows.
- In **web mode** the default bind URL is `http://localhost:5050`. Override it with `--urls` (standard ASP.NET Core host syntax, e.g. `http://*:8080` to listen on all interfaces). On startup the server prints the address plus an `Open: http://…/?token=…` link. You need the **access token** in that link to use the UI (see [Security](#security)).
- `--no-browser` is useful when running the web UI on a headless or remote machine. Open the printed `Open:` link yourself.

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

1. Launch `FileMover -w`. Your default browser opens the link with the token (`http://localhost:5050/?token=…`). If you open `http://localhost:5050` without the token, the UI loads but can't connect. Use the link printed in the console.
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
| **Set permissions (SSH)** | On / `644` | Three-digit octal file mode (`000`–`777`) applied to files written to an SSH destination; validated before the job starts. setuid/setgid/sticky bits are rejected. Created dirs get `755`. |
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

For an **SSH destination** with **Set permissions** enabled, the octal mode (e.g. `644`) is parsed and applied to each uploaded file via SFTP `ChangePermissions`. Directories created during the transfer receive `755`. An invalid mode (not three octal digits, or with setuid/setgid/sticky bits) is rejected before the job starts.

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

- The session filename is a stable SHA-256 hash of the job's source+destination identity, so the *same* job always maps to the *same* session file.
- Location: `%LOCALAPPDATA%\FileMover\sessions\<sha256>.json`.
- On resume, FileMover rebuilds the list of not-yet-completed files and, for each, checks the bytes already present at the destination — if the partial file is smaller than expected it continues **appending** from that exact offset; partial bytes already on disk are credited to the overall progress.
- On full success the session file is **deleted**. On cancel/failure it's **kept** so you can resume.

---

## Authentication

SSH endpoints support two auth methods:

- **Password** — username + password.
- **Private key** — path to a key file (`.pem` / `.ppk`-style); password field is replaced by the key path.

Connections are made with SSH.NET (`SSH.NET 2026.0.0`). Connect/test uses a 15–30 s timeout depending on the operation. The **Test/Connect** action opens and immediately closes an SFTP session to confirm credentials and reachability before you can browse or start.

**Host keys** are verified on a trust-on-first-use basis. The first key a server presents is recorded in `%LOCALAPPDATA%\FileMover\known_hosts`. If a later connection presents a different key, FileMover refuses it as a possible man-in-the-middle attack. Credentials are never written to disk.

---

## Security

Summary below. Full details are in [SECURITY.md](SECURITY.md).

- **Web UI access token.** Each `-w` launch generates a random token. The hub rejects requests without it (`401`) and browser requests from other origins (`403`), so other web pages you have open can't control FileMover. Keep the printed `Open:` link private.
- **Bind address.** The default is `localhost` only. `--urls http://*:port` exposes the UI over **plain HTTP**, so the token and SSH passwords travel unencrypted. Only do this on trusted networks or behind a TLS reverse proxy.
- **SSH host keys** are pinned on first use (see [Authentication](#authentication)).
- **Path traversal protection** stops a malicious SFTP server from writing outside the destination folder.
- **Credentials** stay in memory only.

Report vulnerabilities privately as described in [SECURITY.md](SECURITY.md).

---

## Building from source

Requires the **.NET 10 SDK** (pinned by `global.json`; the project targets `net10.0-windows` with WPF + ASP.NET Core). GNU `make` is optional. Every target is a thin wrapper around `dotnet` or a script in `scripts/`.

```powershell
make help            # list all targets
make build           # build the solution (warnings are errors)
make test            # run the test suite
make run-web         # run the web UI
make debug-desktop   # run the desktop UI as a Debug build
make publish         # single-file EXE → publish\win-x64\FileMover.exe
make publish-arm64   # ARM64 build
make package         # test + publish both runtimes + zip with SHA-256 sums → dist\
make release         # tag v<Version> and create a GitHub release (needs gh CLI)
make audit           # outdated + vulnerable NuGet packages
```

Without `make`, `.\build.ps1` (PowerShell 5.1 compatible) does the same job as `make publish`:

```powershell
.\build.ps1                       # win-x64 Release → publish\win-x64\FileMover.exe
.\build.ps1 -Runtime win-arm64    # ARM64 build
```

Output: one `FileMover.exe` (~167 MB). PDB is embedded; `wwwroot` is embedded as resources; `web.config` generation is suppressed; the static-web-assets pipeline is disabled. The version comes from `<Version>` in `src/FileMover.csproj`.

**Debugging:** in VS Code, `.vscode/launch.json` has *FileMover: Desktop* and *FileMover: Web* launch configurations (press F5). In Visual Studio, open `FileMover.slnx`.

---

## Testing

Tests live in `tests/FileMover.Tests` (xUnit v3 on Microsoft.Testing.Platform).

```powershell
make test        # or: dotnet test --solution FileMover.slnx
make coverage    # writes TestResults\coverage.cobertura.xml
make test-sftp   # also runs the SFTP integration tests (needs Docker)
```

| Area | What's covered |
|---|---|
| Copy engine (Local → Local) | Folder (recursive / flat), single file, multiple files, move + empty-folder cleanup, overwrite on/off, byte-accurate resume, cancellation keeps the session, validation, multi-buffer files |
| SFTP integration | Upload with MD5 verify and chmod (`640` files / `755` dirs), download + move, browsing, `df` free space, rejection of a changed host key |
| Security | Access-token and origin middleware, CLI token parsing, known-hosts store, path traversal guards, shell quoting, permission parsing |
| Models & helpers | Progress formatting, disk-space helpers, session persistence, DTO mapping, hub browse methods |

`make test-sftp` starts a throwaway `linuxserver/openssh-server` container on `127.0.0.1:2222` with a random password, runs the whole suite against it, then removes the container. Without a server, the SFTP tests are skipped.

CI (`.github/workflows/ci.yml`) builds with warnings as errors, runs the tests and fails on vulnerable packages. Dependabot keeps NuGet packages and GitHub Actions up to date.

---

## Architecture

| File | Role |
|---|---|
| `src/FileMover.csproj` | `Microsoft.NET.Sdk.Web`, `net10.0-windows`, WPF + ASP.NET Core, `SSH.NET`. `wwwroot` embedded as resources. |
| `Program.cs` | Entry point. `-d` spins up a WPF `Application` on an STA thread; `-w` builds an ASP.NET Core host with SignalR and an `EmbeddedFileProvider` serving `wwwroot`. |
| `Models.cs` | Core types: `EndpointSettings`, `CopyJob`, `FileEntry`, `LogEntry`, `ProgressSnapshot`, and `DiskSpaceHelper` (free-space + source-size queries). |
| `CopyEngine.cs` | The heart: enumerate files, parallel copy across all local/SSH combos, MD5 verify, chmod, progress/speed/ETA tracking, plus the `SftpPool`. |
| `CopyHub.cs` | SignalR hub for the web UI: `StartCopy`, `Cancel`, `BrowseLocal`, `BrowseSsh`, `TestSshConnection`, `GetDestFreeSpace`, `GetSourceSize`. Includes UNC share enumeration via `NetShareEnum`. |
| `CopySession.cs` | Session persistence — manifest model + atomic save/load/delete keyed by job identity. |
| `SshConnectionFactory.cs` | Builds every SSH/SFTP client with the same timeout and host-key verification. |
| `KnownHostsStore.cs` | Trust-on-first-use host key store (`%LOCALAPPDATA%\FileMover\known_hosts`). |
| `PathHelpers.cs` | Destination path building with traversal checks, SFTP path helpers, shell quoting. |
| `JobValidator.cs` | Job validation (threads, paths, endpoints) and chmod mode parsing. |
| `WebSecurity.cs` | Web-mode CLI options, access-token + origin middleware, security headers. |
| `MainWindow.xaml(.cs)` | WPF desktop UI — charcoal theme, custom title bar, `WM_GETMINMAXINFO` maximize fix. |
| `SftpBrowserWindow.xaml(.cs)` | Desktop SFTP folder-browser dialog. |
| `wwwroot/index.html` | Web UI shell (panels for options, source, destination, progress, log). |
| `wwwroot/app.js` | SignalR client, browse modal, drag handling, all web UI logic. |
| `wwwroot/styles.css` | Dark/light theme via CSS custom properties (`data-theme` toggle). |
| `build.ps1` | `dotnet publish` wrapper. |
| `Makefile`, `scripts/` | Build, test, run, publish and release shortcuts plus their PowerShell helpers. |
| `tests/FileMover.Tests/` | Unit and integration tests. |

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
| **Web UI shows "Connection failed" / no access token** | Open the `Open: …?token=…` link printed in the console, not the bare address. |
| **"Host key … does not match the recorded key"** | The server's key changed. If that's expected (for example the server was rebuilt), delete its line from `%LOCALAPPDATA%\FileMover\known_hosts`. Otherwise treat it as an attack. |
| **Can't see UNC shares** | Type `\\server` (Windows only) to enumerate shares; requires access rights to that server. |

---

## Limitations & notes

- **SSH→SSH** direct transfers are intentionally unsupported.
- **SSH source size** isn't measured, so the pre-copy "source larger than free space" warning only fires for **local** sources.
- The **web UI runs a single job at a time** process-wide.
- The desktop UI is **Windows-only** (WPF); the published build targets `net10.0-windows`.
- Upgrading from 1.0.x: session files are now named by SHA-256, so transfers interrupted under 1.0.x start fresh instead of resuming.
- Sessions live under `%LOCALAPPDATA%\FileMover\sessions\`; deleting them simply means a future identical job starts fresh instead of resuming.
- `df` free-space query: `df -Pk '<path>' | awk 'NR==2 {print $4}'` returns KB and is multiplied by 1024.

---

## License

[MIT](LICENSE)
