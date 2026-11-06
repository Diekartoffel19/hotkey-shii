# AppSwitch

Tiny Windows app: press a global hotkey and instantly switch to a chosen app — also from fullscreen games.

## Features
- Record any key combo, pick the target from currently open windows
- Optional launch path if the app is not running
- Press again to minimize (toggle)
- Lives in the taskbar, optional autostart with Windows
- Per-user installer, no admin rights needed

## Build
Run `build.bat` (uses the .NET Framework `csc.exe` built into Windows, no SDK needed).

Output:
- `dist\AppSwitch-Setup.exe` – installer
- `dist\AppSwitch.exe` – portable version

Version and publisher: `Version.cs`.

## Notes
- If the target game runs as admin, AppSwitch must run as admin too.
- For exclusive-fullscreen games, borderless windowed mode switches fastest.
- Settings: `%APPDATA%\AppSwitch\keybinds.ini`
