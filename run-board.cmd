@echo off
rem Launch the board.
rem
rem Working directory decides nothing here: the board is found by searching upward
rem from the executable, so this can be launched from anywhere. Pass a .canvas file
rem to open that one instead.
rem
rem If Windows reports a missing component, the Windows App Runtime is not installed
rem (README.md, "运行时").

setlocal
cd /d "%~dp0"

set "BOARD=src\Outspread.Board\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\Outspread.Board.exe"

if not exist "%BOARD%" (
    echo [outspread] board is not built yet.
    echo [outspread] run: dotnet build src\Outspread.Board\Outspread.Board.csproj -c Debug -p:Platform=x64
    pause
    exit /b 1
)

start "" "%BOARD%" %*
endlocal
