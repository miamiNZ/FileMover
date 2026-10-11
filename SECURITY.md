# Security policy

## Reporting a vulnerability

Please **do not open a public issue** for security problems. Report them privately through
GitHub's [private vulnerability reporting](../../security/advisories/new) for this repository.
Include steps to reproduce and the FileMover version (`FileMover.exe` → Properties → Details).

## Security model

FileMover is a single-user tool that runs on your own machine. It can read, write and delete any
file your Windows account can, and it holds the SSH credentials you type in. The defences below
protect that access.

### Web UI (`-w`)

- **Per-launch access token.** On startup FileMover prints a URL ending in `?token=…` and opens it.
  Every SignalR request must carry that token (in the `Authorization` header or the `access_token`
  query value). Requests without it get `401`. Use `--token <value>` (16+ characters) to set a fixed
  token, for example on a headless machine.
- **Origin check.** Browser requests whose `Origin` does not match the server host get `403`. Together
  with the token this blocks cross-site WebSocket hijacking and DNS-rebinding attacks from other
  web pages you have open.
- **Loopback by default.** The server listens on `http://localhost:5050`. If you bind to other
  interfaces (`--urls http://*:8080`), traffic is **plain HTTP**: the token and any SSH passwords
  cross the network unencrypted. Only do this on networks you trust, or put a TLS reverse proxy in front.
- **Security headers.** All responses send `Content-Security-Policy`, `X-Frame-Options: DENY`,
  `X-Content-Type-Options: nosniff` and `Referrer-Policy: no-referrer`.
- The token is removed from the address bar after the page loads, and framework request logging is
  turned down so the token isn't written to the console.

### SSH / SFTP

- **Host key verification (trust on first use).** The first key a server presents is saved to
  `%LOCALAPPDATA%\FileMover\known_hosts`. If a later connection presents a different key of the same
  type, FileMover refuses to connect (possible man-in-the-middle). If the server really was re-keyed,
  delete its line from that file.
- **Credentials are never stored.** Passwords and key paths live in memory only for the session.
  Session files (`%LOCALAPPDATA%\FileMover\sessions\`) contain file paths and progress only.
- **Remote commands are shell-quoted.** `md5sum` and `df` paths are single-quoted with embedded quotes
  escaped.
- **Path traversal protection.** File names returned by a remote server are checked so that `..`,
  absolute paths or separators can never write outside the chosen destination folder.
- **chmod is limited to `000`–`777`.** setuid, setgid and sticky bits are rejected.

### Integrity

MD5 is used only to check that a copy matches its source (it matches what `md5sum` gives on the
remote side). It protects against corruption, not against deliberate tampering.

## Supported versions

Only the latest release receives security fixes.
