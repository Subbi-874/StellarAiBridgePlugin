@echo off
cd /d "%~dp0mcp_bridge"
"%~dp0mcp_bridge\.venv\Scripts\python.exe" server.py %*
