@echo off
cd /d "%~dp0"
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist
if errorlevel 1 (
  echo Build failed
  pause
  exit /b 1
)
echo.
echo Done: dist\SpotifyLyricsWallpaper.exe
explorer dist
pause
