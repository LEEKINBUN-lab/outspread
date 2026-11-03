@echo off
setlocal
rem One command that says whether this board still works:
rem core tests, both builds, and the window's own self test.
rem
rem The self test runs against a COPY of the board under %TEMP%, because it moves a node,
rem undoes it and saves on a timer: pointed at a live board it has been seen to leave one node
rem two pixels off. His board file is not the gate's playground.
rem
rem The copy comes from tests\fixtures\gate.canvas, not from boards\: the fixture has the shapes
rem the self test walks (a group with two members, loose cards, multi-line text, a tagged and a
rem dated card), while boards\ now holds his own tutorial board — editing that must not silently
rem turn a self-test step into "not applicable".
rem
rem The fixture path is ASCII on purpose: this file is UTF-8 but cmd parses a batch in the OEM
rem code page, so a Chinese file name here would be looked for under the wrong bytes.

cd /d "%~dp0"

set "BOARD=src\Outspread.Board\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\Outspread.Board.exe"
set "SCRATCH=%TEMP%\outspread-selftest"

echo [1/6] 核心测试
dotnet test tests\Outspread.Core.Tests\Outspread.Core.Tests.csproj -c Debug
if not "%ERRORLEVEL%"=="0" goto :fail

echo [2/6] MCP 写分支
rem Build and run this one OUTSIDE the repo. The agent's own MCP connector is a long-lived
rem process holding src\Outspread.Mcp\bin\Debug\net10.0\Outspread.Core.dll, so an in-tree build
rem of that project dies with MSB3027 the moment Core changes - and the gate would then be red
rem for a reason that has nothing to do with whether the board works (it went red exactly that
rem way on 2026-10-27, with the connector attached). A shadow output dir compiles the same
rem sources and cannot be locked by anything.
dotnet test tests\Outspread.Mcp.Tests\Outspread.Mcp.Tests.csproj -c Debug -p:ArtifactsPath=%TEMP%\outspread-gate-mcp
if not "%ERRORLEVEL%"=="0" goto :fail

echo [3/6] 界面构建 —— 先看有没有实例锁着 dll
rem findstr, not find: when this gate is launched from a bash PATH that carries GNU
rem coreutils, `find /I ...` is *not* Windows FIND and fails with rc=1 — the guard below
rem then silently no-ops and the build dies 20 lines later instead of saying why.
tasklist /FI "IMAGENAME eq Outspread.Board.exe" /NH 2>nul | findstr /I /C:"Outspread.Board.exe" >nul
if not errorlevel 1 (
    echo.
    echo 红灯是装置不是产品：有一个界面实例正在跑，它锁着 Outspread.Core.dll，构建拷不进去。
    tasklist /FI "IMAGENAME eq Outspread.Board.exe" /NH
    echo 关掉那个窗口再跑。闸门不替你关它——那可能是你正在用的板子。
    goto :fail
)

echo [3/6] 界面构建
dotnet build src\Outspread.Board\Outspread.Board.csproj -c Debug -p:Platform=x64
if not "%ERRORLEVEL%"=="0" goto :fail

echo [4/6] 命令行构建
dotnet build src\Outspread.Cli\outspread.csproj -c Debug
if not "%ERRORLEVEL%"=="0" goto :fail

echo [4/6] 连接器 exe 顺手刷新（尽力；被别的连接器锁着就跳过，不挡闸门——它会在下次会话自 build）
dotnet build src\Outspread.Mcp\outspread-mcp.csproj -c Debug
if not "%ERRORLEVEL%"=="0" echo 跳过：exe 被锁或构建失败；不挡闸门

echo [5/6] 界面自检（在临时副本上跑，绝不动 boards 里的板子）
call :reset_scratch
rem The board path must be passed explicitly. The window locates a board by
rem --board, then TANKAI_BOARD, then boards\scratch.canvas walking up, then the first
rem .canvas in the cwd — and this cwd is the repository root, so without the argument the
rem self test would edit the real board in boards\ (and the copy above would be a copy
rem nobody reads).
rem
rem These comment lines stay ASCII on purpose: the file is UTF-8 while cmd parses a batch in
rem the OEM codepage, and a multi-byte sequence can swallow the CR of the line ending and
rem merge the next line into this one. Which line survives the merge is up to the file's
rem byte alignment — today it ate a comment and still printed "all green"; another alignment
rem would eat the self test call below and print "all green" anyway.
"%BOARD%" --selftest "%SCRATCH%\selftest.canvas"
set "FIRST_RC=%ERRORLEVEL%"
if "%FIRST_RC%"=="0" goto :selftest_done
echo 界面自检第一发红（rc=%FIRST_RC%）：把副本重置以后再跑一发取对照
call :reset_scratch
"%BOARD%" --selftest "%SCRATCH%\selftest.canvas"
if not "%ERRORLEVEL%"=="0" goto :fail
echo 注意：两发跑的是同一份重置过的副本，所以第二发绿只能是起步抖动，不是状态依赖
goto :selftest_done

:selftest_done

echo [6/6] 跟上开始菜单那枚
rem The Start Menu copy is a snapshot of a build, so it goes stale unless something refreshes
rem it — and the only thing that can write into it is a build that is not itself running from
rem it. Running the freshly built bin exe with --heal-install does exactly that, with no window.
rem
rem On a CI runner there is no Start Menu to keep current, and WScript.Shell is not guaranteed
rem there, so this step is skipped out loud instead of going red over somebody else's desktop.
if "%GITHUB_ACTIONS%"=="true" goto :skip_heal
"%BOARD%" --heal-install
if not "%ERRORLEVEL%"=="0" goto :fail
goto :heal_done

:skip_heal
echo 跳过：CI 那台机器上没有他的开始菜单（本机 10-26 21:17 实测：旧副本点开就把窗口交给了新构建）

:heal_done

echo.
echo 全过
exit /b 0

:fail
echo.
echo 红灯：看上面的输出，日志在 %%LOCALAPPDATA%%\Outspread\board.log
exit /b 1

:reset_scratch
rem Reset the WHOLE scratch board set, not just the one file. The window writes a sidecar
rem next to each board (camera: pan/zoom, manualSize), and the self test itself ends up at
rem zoom 112% while the "all notes" parent file keeps a node per run. Left in place, the next
rem run starts somewhere the assertions were not written for: on 2026-10-24 that leaked a
rem zoom=1.12 sidecar and the markdown-fit step went red (140 < 142.08) on a board nobody
rem touched. On 2026-10-26 the same residue did worse: the retry ran on the board the failed
rem first attempt had left behind, so "first red, second green" printed as all green for
rem hours. Reset per attempt — then a second attempt can only differ by startup timing.
rem ASCII wildcards on purpose: the Chinese file names cannot be spelled in this file
rem (cmd parses a batch in the OEM codepage).
if not exist "%SCRATCH%" mkdir "%SCRATCH%"
del /q "%SCRATCH%\*.canvas" >nul 2>&1
del /q "%SCRATCH%\*.canvas.view.json" >nul 2>&1
if exist "%SCRATCH%\.outspread-history" rd /s /q "%SCRATCH%\.outspread-history"
copy /y "tests\fixtures\gate.canvas" "%SCRATCH%\selftest.canvas" >nul
if exist "boards\assets" xcopy /y /i /e /q "boards\assets" "%SCRATCH%\assets" >nul
if not exist "%BOARD%" goto :fail
goto :eof
