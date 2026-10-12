# Security policy

## What this product is, so you can judge the threat model

Outspread is a **local desktop app** (WinUI 3, Windows 10+, x64, unpackaged).
It reads and writes board files in the JSON Canvas 1.0 format (`.canvas`, plain
JSON) plus an asset folder next to the board. It has one optional network
surface: syncing a single board to a **GitHub repository through the GitHub
Contents REST API** (`https://api.github.com/`, see
`src/Outspread.Core/GitHubContents.cs:39`) using a token the *user* supplies.
That token is kept in the Windows Credential Manager as a generic credential
target `outspread.sync.github`, persisted per machine, written through
`CredWriteW` (`src/Outspread.Core/SyncCredentials.cs:21,27-28,104`); it is not
stored in the board folder or in a config file. There is no server operated by
me, no accounts, and no telemetry of any kind.

## The local surface, stated from the code

The "screenshot server" is **not HTTP**. There is no `HttpListener`, no
`TcpListener`, and no port: the app opens a **Windows named pipe**, one per
open board.

- Pipe name: `outspread-shot-<16 hex>` where the hex is the first 16
  characters of the SHA-1 of the lowercased full board path
  (`src/Outspread.Core/ShotProtocol.cs:25-31`), reached as
  `\\.\pipe\outspread-shot-<16 hex>`.
- The pipe is created with `NamedPipeServerStream` defaults — byte mode, one
  server instance, **no explicit `PipeSecurity` and no application-level
  authentication** (`src/Outspread.Board/ShotServer.cs:53-58`). Access is
  governed only by the operating system's default pipe permissions; within
  reach of processes on the machine that the OS grants to the pipe.
- Each connection supplies one JSON request line (`ShotServer.cs:62-79`); the
  board renders itself and **writes a PNG to the output path named in the
  request** (`ShotProtocol.cs:82-88`, handler at
  `src/Outspread.Board/MainWindow.xaml.cs:3150-3168`). A local process that
  can open the pipe can therefore make the board capture its own window and
  write image files wherever the board process has write access. It cannot, by
  this protocol, mutate the board document itself.

The classes I consider most interesting for reports, in rough order:

1. Path handling when opening boards, dragged-in assets, and the shot `out`
   path above.
2. The `.canvas` JSON parser and the sidecar view-state file (malformed or
   hostile board files).
3. The sync layer's treatment of the stored token and of responses from the
   GitHub API.
4. The shot pipe described above.

## How to report

Report privately first. Use GitHub's private vulnerability reporting on this
repository: <https://github.com/LEEKINBUN-lab/outspread/security/advisories/new>.
If that is not enabled, open a blank issue with only the words "security
contact request" and no details, and I will arrange a private channel.

Do not include your own secrets in a report — the log file and board files can
carry paths and repository names; paste the minimum needed. Do not post an
issue describing an exploitable flaw before a fix exists.

## What to expect

I am a solo maintainer. I will acknowledge a private report as fast as I
can, which is best-effort and is **not an SLA** — do not schedule around a
guaranteed response time. If a report is confirmed, the fix ships in the next
release together with a GitHub security advisory, and I will credit the
reporter unless they prefer not to be. If you disagree with my severity
assessment you are of course free to disclose after a reasonable delay; I would
rather that not be a surprise, but I will not pretend to control it.

One line for readers of the Chinese README: 用法与说明请看 `README.md`。
