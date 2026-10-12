# Support

Outspread (铺陈) is an infinite-canvas board for Windows: notes, images and
links on a plane, stored as plain JSON Canvas 1.0 files you own. I make it
alone, in my spare time, and distribute it as free open-source software under
the MIT licence.

## How to ask for help

Open a GitHub issue: <https://github.com/LEEKINBUN-lab/outspread/issues>.
Questions, bugs, and feature requests are all fine. There is no other support
channel; if security is involved, use `SECURITY.md` instead of an issue.

## What a useful bug report contains

- **What you did, what happened, what you expected.** In one paragraph is
  enough; I will ask if I need more.
- **The log file.** The board process writes a diagnostics log next to itself,
  outside the repository, at
  `%LocalAppData%\Outspread\board.log` (that is `<user>\AppData\Local\Outspread\board.log`).
  The path and on/off switch are set in `src/Outspread.Board/Diagnostics.cs`:
  set the environment variable `TANKAI_DIAGNOSTICS` to a file path to redirect
  it, or `TANKAI_DIAGNOSTICS=0` to silence it. Trim anything you do not want
  public before attaching.
- **Which board file was actually opened.** The app picks a board in this
  order (`src/Outspread.Core/BoardLocator.cs`): an explicit `--board` path,
  then the `TANKAI_BOARD` environment variable, then the nearest
  `boards/scratch.canvas` while walking up from the working directory, then
  the first `.canvas` file found walking up, and finally it creates
  `%LocalAppData%\Outspread\boards\scratch.canvas`. Saying "the board I think
  it opened" is not enough; the CLI prints the resolution reason, so paste
  that line if you can.
- **Windows version and app build**, and whether you are running the
  framework-dependent build (the default: it needs the Windows App Runtime
  installed machine-wide).
- **The board file itself.** Board data is plain JSON (`.canvas`), so you can
  attach it or diff it against a known-good version; that is usually the
  single most useful artifact. Copy it first if it contains private notes.

## Reproducing it yourself

- `check.cmd` at the repository root is the whole gate: core tests, both
  builds, and the window's own self test. The self test deliberately runs
  against a **copy** of the board under `%TEMP%\outspread-selftest`, never
  your real `boards\scratch.canvas`. If `check.cmd` is red, say so and paste
  the failing step; it prints which of the four stages broke.
- For UI bugs, reproduce on the scratch board (`boards\scratch.canvas`, run
  via `run-board.cmd`) before filing, so we are talking about the same file.

## The command line

`src/Outspread.Cli` builds `outspread.exe`, the board's command line, meant
for scripting and for AI agents acting on a board: `list`, `show`, `add`,
`patch`, `rm`, `validate`, `new`, `shot` (screenshot an open board through its
named pipe), and `sync` (`link` / `status` / `once` / `forget`) for the GitHub
board sync. It mutates the same file the window has open through the same
locking path, so a human and a script can write in the same second. Run
`outspread help` for the shapes; output is in Chinese by design.

## What is not supported

There is no warranty of any kind, as the licence says. I have no obligation to
respond to issues, to fix anything, or to accept any feature request. The app
is distributed as-is; if it eats a note, the honest answer is that the board
file is plain JSON and your backup is your responsibility. Sync talks to your
GitHub account with your token — account-side problems go to GitHub, not me.
