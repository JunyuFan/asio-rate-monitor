# ASIO Rate Monitor

A lightweight Windows utility that displays the current sample rate reported by the Dreamus USB Audio Device driver, even when its control panel is hidden.

- Persistent tray indicator and optional taskbar text, such as **44.1 kHz** or **48 kHz**.
- Event-driven updates, with selectable 1, 2, or 5 second polling and automatic fallback when notifications are unavailable.
- Reads driver status without creating audio streams or changing audio settings.

![效果图](figs/preview.png)

## Requirements

Windows 10 (1607 or later) or Windows 11, x64; .NET Framework 4.8.

Currently only compatible with Astell&Kern (Dreamus) drivers; other ASIO drivers are unsupported.

## Usage

Extract the release to a writable folder and run `ASIORateMonitor.exe`. Keep its `.config` file alongside it. Right-click the tray icon to choose the update mode, toggle taskbar text, or exit; double-click to view status. Preferences are saved to `settings.ini` beside the executable.

Taskbar text appears beside the weather widget or notification area on the primary horizontal taskbar. Placement depends on the Windows taskbar layout; the tray indicator remains available. `-- kHz` means the rate is unavailable. If event updates stop, select polling.

## Build

Run from PowerShell with no application instance running from the output folder:

```powershell
.\build.ps1
```

The script uses the C# compiler supplied with .NET Framework, runs 35 automated checks, and creates source and portable release archives in `dist/`. No NuGet packages, ASIO SDK, or vendor header files are required.

Driver extensions implement `ISampleRateProvider`; event-capable providers also implement `IChangeNotifications`.

## License

Licensed under the [MIT License](LICENSE). See [NOTICE](NOTICE) for the scope of this license and external runtime components.
