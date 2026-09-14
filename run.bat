@echo off
rem ---------------------------------------------------------------
rem  اجرای بارگو روی http://localhost:5810 — دابل‌کلیک کافی است
rem  پنجره را باز نگه دارید؛ با بستن آن برنامه هم بسته می‌شود.
rem ---------------------------------------------------------------
cd /d "%~dp0src\Bargo.Web"
set ASPNETCORE_ENVIRONMENT=Development
dotnet run
pause
