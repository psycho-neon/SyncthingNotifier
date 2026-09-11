# Syncthing Notifier for Windows

A lightweight .NET 10 tray application that monitors the Syncthing folders configured on the **local Windows PC**. It shows a Windows notification when a folder has finished syncing, whether the change was made locally or arrived from another Syncthing device.

For example, if the `Configurations` folder maps to `D:\Config`, a completed synchronization displays:

> Configurations - synchronized

## Requirements

- Windows 10 or Windows 11, x64
- Syncthing running on this PC with its Web GUI enabled (the usual `http://127.0.0.1:8384`)
- .NET 10 Windows Desktop Runtime (x64) installed on each PC that runs the published application
- .NET 10 SDK only when building from source

The application reads Syncthing's local configuration from:

```text
%LOCALAPPDATA%\Syncthing\config.xml
```

It uses the GUI API key stored there and monitors every folder in that configuration. No password, server address, or API key needs to be entered into this application. The Syncthing instance on Proxmox is synchronized through the local Syncthing client; this notifier deliberately queries that local client, which is the authoritative source for whether the PC is fully in sync.

## Run while developing

```powershell
dotnet run --project .\SyncthingNotifier.csproj
```

The program has no main window. Find its Syncthing icon in the notification area; Windows may place it under the hidden-icons arrow.

## Publish

From this directory, run:

```powershell
.\publish.ps1
```

The deployment artifact is:

```text
.\publish\SyncthingNotifier.exe
```

It is a compressed, framework-dependent, single-file `win-x64` executable. It has no NuGet or side-by-side application DLLs to distribute, but requires the separately installed .NET 10 Windows Desktop Runtime (x64).

## Tray menu

- **Open Web GUI** — opens the local Syncthing GUI from `config.xml`.
- **Start on Boot** — enables/disables automatic start for the current Windows user via `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.
- **Send Test Notification** — verifies that Windows can show notifications.
- **About** — shows application version and current monitoring status.
- **Quit** — stops monitoring and exits.

## Synchronization detection

The notifier first records the current completion state without announcing already-synced folders. It then combines Syncthing's `StateChanged` and `FolderCompletion` events with a two-second completion check. A notification is sent only once when a monitored folder transitions back to fully synchronized.

If the local Syncthing GUI is stopped, disabled, or inaccessible, the tray tooltip and a warning notification explain the problem. The notifier retries every 15 seconds.
