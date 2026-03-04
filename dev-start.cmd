@echo off
setlocal

rem Starts API + client + admin for local development (3 separate terminals).
rem Usage:
rem   dev-start.cmd

set "DOTNET_ROOT=%~dp0"
set "FRONTEND_ROOT=%DOTNET_ROOT%..\sabr-frontend"

if not exist "%DOTNET_ROOT%src\Sabr.Api\Sabr.Api.csproj" (
  echo Could not find API project at "%DOTNET_ROOT%src\Sabr.Api\Sabr.Api.csproj"
  exit /b 1
)

if not exist "%FRONTEND_ROOT%\package.json" (
  echo Could not find frontend at "%FRONTEND_ROOT%"
  exit /b 1
)

set "NPM_CMD=C:\Program Files\nodejs\npm.cmd"
if not exist "%NPM_CMD%" (
  echo Could not find npm at "%NPM_CMD%". Update NPM_CMD in dev-start.cmd.
  exit /b 1
)

echo Starting SABR API (http://localhost:5250)...
start "SABR API" cmd /k "cd /d "%DOTNET_ROOT%" && set ASPNETCORE_ENVIRONMENT=Development && dotnet run --project "src\Sabr.Api\Sabr.Api.csproj""

echo Starting SABR Client (http://localhost:4200)...
start "SABR Client" cmd /k "cd /d "%FRONTEND_ROOT%" && "%NPM_CMD%" run start:client"

echo Starting SABR Admin (http://localhost:4300)...
start "SABR Admin" cmd /k "cd /d "%FRONTEND_ROOT%" && "%NPM_CMD%" run start:admin"

echo.
echo Done. Open:
echo   Client: http://localhost:4200/login
echo   Admin : http://localhost:4300/login
echo.
echo Note: in DEV, login 429 may still happen after many failed attempts.
echo Wait for cooldown (about 1 minute) or restart the API process.
echo.
