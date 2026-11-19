@echo off
setlocal
set "ROOT=%~dp0"
set "PYTHON_EXE="

if exist "%ROOT%runtime\python.exe" (
    set "PYTHON_EXE=%ROOT%runtime\python.exe"
) else if exist "%ROOT%mcp_bridge\runtime\python.exe" (
    set "PYTHON_EXE=%ROOT%mcp_bridge\runtime\python.exe"
) else if exist "%ROOT%mcp_bridge\.venv\Scripts\python.exe" (
    set "PYTHON_EXE=%ROOT%mcp_bridge\.venv\Scripts\python.exe"
) else (
    for %%I in (python.exe) do set "PYTHON_EXE=%%~$PATH:I"
)

if "%PYTHON_EXE%"=="" (
    echo [StellarAgyBridge] Python executable not found.
    echo Place the portable runtime under "%ROOT%runtime\" or set up "%ROOT%mcp_bridge\.venv\".
    exit /b 1
)

cd /d "%ROOT%mcp_bridge"
"%PYTHON_EXE%" server.py %*
