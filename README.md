# Aria2NET

A Windows download manager in the style of Internet Download Manager, built with WinUI 3 on top of [aria2](https://aria2.github.io/). aria2 does the transfers; Aria2NET is the interface around it.

> **Status: early development (0.3).** Usable for pasted links, but there is no history yet (downloads in progress are lost when the app closes), no torrents and no tray icon. See the [roadmap](docs/ROADMAP.md).

## What works today
- Paste one or many links and download them with multiple connections (aria2c runs as a hidden child process).
- Live list with progress, speed, size and time left.
- Pause, resume, retry, remove (with a 5 second Undo) and remove with the file; finished files go to the Recycle Bin.
- Multi-select with keyboard shortcuts (Del, Shift+Del, Space, Enter, Ctrl+A), filter tabs, sortable columns and a right-click menu.
- Per-download speed limit, rename, change folder, queue order and "start now".
- Plain-language messages for aria2 errors.

## Install
Releases are on the [Releases page](../../releases): `Aria2NET-<version>-win-x64.zip` or `win-arm64.zip`. Unzip anywhere and run `Aria2NET.exe`. No .NET or Windows App SDK install is needed, and there is no installer yet.

The exe is **not code signed** yet, so Windows SmartScreen may warn on first run. Compare the zip with the `.sha256` file next to it. Signing is planned (see the roadmap).

Requires Windows 10 1809 or later (x64 or ARM64). The ARM64 build runs the x64 `aria2c.exe` under emulation.

## Build from source
Needs the .NET 10 SDK and the Windows App SDK workload for Visual Studio 2022+ (or just the SDK for the command line).

```
pwsh -ExecutionPolicy Bypass -File tools/get-aria2.ps1   # downloads aria2c (pinned version, SHA-256 checked)
dotnet build Aria2NET.slnx -p:Platform=x64
dotnet test Aria2NET.Tests
dotnet run --project Aria2NET -p:Platform=x64
```

`dotnet test` also runs tests against the real `aria2c.exe`; they are skipped if the script has not been run.

## Project layout
| Folder | What it is |
|---|---|
| `Aria2NET` | WinUI 3 app: window, pages, view models |
| `Aria2NET.Core` | UI-free logic: aria2 process and RPC, settings, download rules |
| `Aria2NET.Tests` | Unit tests and integration tests with a real aria2c |
| `tools` | Scripts to fetch aria2 and package a release |
| `docs` | [Roadmap](docs/ROADMAP.md), [architecture](docs/ARCHITECTURE.md), [releasing](docs/RELEASING.md) |

## Releasing
Maintainers publish a release by running the **release** workflow from the Actions tab; see [docs/RELEASING.md](docs/RELEASING.md).

## License
[GPL-3.0](LICENSE.txt). aria2 is GPL-2.0-or-later and is downloaded separately, not stored in this repository.
