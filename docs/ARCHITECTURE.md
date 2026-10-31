# Architecture

## Projects
| Project | What it is |
|---|---|
| `Aria2NET.Core` | Plain .NET library, no UI. App paths, settings store, aria2 config, RPC client, engine (process owner), file logger. |
| `Aria2NET` | WinUI 3 app (unpackaged, self-contained). DI setup, window, pages, view models. |
| `Aria2NET.Tests` | xUnit. Unit tests plus integration tests that start the real `aria2c.exe` (skipped if it hasn't been fetched). |

Keep UI types out of `Aria2NET.Core` so the logic stays testable.

## Process model (0.1)
The app owns a hidden `aria2c.exe` child process.

- On launch `Aria2Engine` writes `%LocalAppData%\Aria2NET\engine\aria2.conf` with a random loopback port and a random secret, then starts `aria2c --conf-path=...`. The secret lives only in that file (not on the command line).
- RPC is JSON-RPC over HTTP on `127.0.0.1`. Readiness = `aria2.getVersion` answers.
- `stop-with-process=<our pid>` makes aria2 exit on its own if the app is killed.
- On close the window cancels the close, calls `saveSession` + `shutdown`, waits up to 5 s (then kills), and closes.
- On startup any stray `aria2c.exe` from our own tools folder is killed.
- Unexpected exit → state `Crashed`, shown as an error bar with "Restart engine". Automatic restart is planned for 0.4.

## Downloads data flow (0.2)
- `DownloadMonitor` (Core) ticks once per second and asks `Aria2RpcClient.PollAsync()`, which sends **one** `system.multicall` (`tellActive`, `tellWaiting`, `tellStopped`, `getGlobalStat`, each with the secret token inside the call and a `keys` filter).
- The snapshot is raised on a background thread. `DownloadsViewModel` hops to the UI thread and updates `DownloadRowViewModel`s **in place by GID** (no list rebuild, so no flicker and scroll is kept).
- The monitor reads `engine.Rpc` on every tick and only polls while the engine is `Running`, so engine restarts need no re-wiring. While the engine is down the list is cleared and Add is disabled.
- Adding: `UrlParser` validates pasted text, then `AddUriAsync` is called per URL with `dir` (and `out` for a single URL).
- Gotcha: passing a `string[]` as the only argument to a `params object?[]` method binds the array to `params` itself. Cast to `object` (see `tellActive`).

## Controlling downloads (0.3)
- **Where the rules live (Core, no UI):** `DownloadCapabilities` decides which action is allowed for a status (menus, command bar and shortcuts all read it), `Aria2Errors` maps aria2 error codes to text, `DownloadView` does filter + sort on plain `DownloadItem`s, and `DownloadOperations` is the one service that talks to aria2 for pause/resume/remove/retry/move/rename/limit. All of it is tested without WinUI; `DownloadOperations` is also tested against a real aria2c.
- **Batches:** acting on several rows sends one `system.multicall` (token inside each call). A fault in one entry is reported but does not stop the others.
- **Remove:** live downloads get `forceRemove`, then every removed GID gets `removeDownloadResult` so aria2 forgets it. "Remove from list" hides the rows immediately and only sends the RPC after 5 s (`Undo`); closing the window finalizes it first. "Remove and delete file" sends finished files to the Recycle Bin and deletes partial files (and their `.aria2` control file) outright.
- **List pipeline:** `Apply(snapshot)` → keep one `DownloadRowViewModel` per GID → `DownloadView.Apply` (filter, sort) → `SyncRows` moves/inserts/removes rows in place. Moving a row makes WinUI's ListView forget its selection (and it reports that *during* the move), so the view model captures the selection first, ignores selection events while syncing, and the page re-selects afterwards (`SelectionRestoreNeeded` / `FinishRestore`).
- **Force start:** aria2 cannot bypass `max-concurrent-downloads`; the item is moved to the front and the limit is raised by one until it leaves Active/Waiting (`DownloadOperations.OnSnapshot` puts it back).
- **Gotchas found by real aria2:** a resumed download sends `Range: bytes=N-M` (M present), a test server that only understands `bytes=N-` answers 200 and aria2 fails with "No URI available" (error 8). aria2 reports only the original URL after a redirect, and an existing complete file with the same name makes a new download "complete" at once (`continue=true`).

## Single instance
`Program.cs` (custom `Main`, `DISABLE_XAML_GENERATED_MAIN`) registers an `AppInstance` key. A second launch redirects to the first, which brings its window to the front.

## Data locations
- `%LocalAppData%\Aria2NET\settings.json`, `logs\`, `engine\` (conf, session).
- aria2 itself: `<app folder>\tools\aria2\aria2c.exe`.

## Why unpackaged
No MSIX means no registry/file virtualization, easy native messaging for the browser extension (2.0), easy launching of bundled tools, and a plain installer or zip.

## Getting started
```
pwsh -ExecutionPolicy Bypass -File tools/get-aria2.ps1
dotnet build Aria2NET.slnx -p:Platform=x64
dotnet test Aria2NET.Tests
dotnet run --project Aria2NET -p:Platform=x64
```
`tools/aria2/` is git-ignored. The script pins aria2 1.37.0 and verifies a SHA-256.
