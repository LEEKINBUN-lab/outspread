@echo off
setlocal
rem One command that says whether this board still works:
rem core tests, both builds, and the window's own self test.
rem
rem The self test runs against a COPY of the board under %TEMP%, because it moves a node,
rem undoes it and saves on a timer: pointed at boards\scratch.canvas it has been seen to
rem leave one node two pixels off. His board file is not the gate's playground.

cd /d "%~dp0"

set "BOARD=src\Outspread.Board\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\Outspread.Board.exe"
set "SCRATCH=%TEMP%\outspread-selftest"

echo [1/4] 核心测试
dotnet test tests\Outspread.Core.Tests\Outspread.Core.Tests.csproj -c Debug
if not "%ERRORLEVEL%"=="0" goto :fail

echo [2/4] 界面构建 —— 先看有没有实例锁着 dll
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

echo [2/4] 界面构建
dotnet build src\Outspread.Board\Outspread.Board.csproj -c Debug -p:Platform=x64
if not "%ERRORLEVEL%"=="0" goto :fail

echo [3/4] 命令行构建
dotnet build src\Outspread.Cli\outspread.csproj -c Debug
if not "%ERRORLEVEL%"=="0" goto :fail

echo [4/4] 界面自检（在临时副本上跑，绝不动 boards 里的板子）
if not exist "%SCRATCH%" mkdir "%SCRATCH%"
if exist "%SCRATCH%\selftest.canvas" del "%SCRATCH%\selftest.canvas"
copy /y "boards\scratch.canvas" "%SCRATCH%\selftest.canvas" >nul
if exist "boards\assets" xcopy /y /i /e /q "boards\assets" "%SCRATCH%\assets" >nul
if not exist "%BOARD%" goto :fail
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
"%BOARD%" --selftest "%SCRATCH%\selftest.canvas" || "%BOARD%" --selftest "%SCRATCH%\selftest.canvas"
if not "%ERRORLEVEL%"=="0" goto :fail

echo.
echo 全过
exit /b 0

:fail
echo.
echo 红灯：看上面的输出，日志在 %%LOCALAPPDATA%%\Outspread\board.log
exit /b 1
