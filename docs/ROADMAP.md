# Aria2NET Roadmap

A WinUI 3 download manager (IDM-style) on top of aria2c. aria2 does the transfers. Aria2NET is the UI, the background behavior, and the automation around it. Licensed GPL-3.0 (same family as aria2).

## Decisions already made

| Topic | Decision |
|---|---|
| Target | .NET 10, WinUI 3 (done, builds) |
| Trimming | Off (done). No more trimming problems. |
| Packaging | **Unpackaged** (plain .exe, done). No MSIX. Reason: MSIX is what makes browser-extension setup, launching aria2c, and Windows startup awkward. Unpackaged is a normal Windows app and none of that is a problem. |
| WinAppSDK | Self-contained (users don't need to install a runtime) |
| License | GPL-3.0 (LICENSE.txt already is) |
| Browser extension | v2.0 |
| aria2 | Bundled `aria2c.exe`, run as a hidden child process, controlled over its JSON-RPC interface (localhost, random port, random secret) |
| Stack | CommunityToolkit.Mvvm, Microsoft.Extensions.DependencyInjection/Hosting, SQLite, System.Text.Json (source generated) |

## Version map

| Version | Theme | You can... |
|---|---|---|
| 0.1 | Skeleton | Launch the app, aria2 starts and stops with it |
| 0.2 | First download | Paste a URL, see it download |
| 0.3 | Control | Pause, resume, remove, open file/folder |
| 0.4 | Persistence | Close and reopen without losing anything; history |
| 0.5 | Torrents | Magnet links, .torrent files, pick files |
| 0.6 | Lives in background | Tray, notifications, start with Windows |
| 0.7 | Settings | Speed limits, folders, connections, proxy |
| **0.8 = MVP** | Daily-usable | Use it instead of the browser's downloader for pasted links and torrents |
| 0.9 | Hardening | Bug fixing, tests, polish, installer |
| **1.0** | Full feature release | Everything below the 1.0 section |
| 1.x | Post-1.0 polish | Smaller extras |
| **2.0** | Browser capture | Chrome/Edge/Firefox send downloads to Aria2NET |
| 2.x | Browser extras | Media sniffer, bulk grab |
| **3.0** | Automation | Rules, scheduler-on-steroids, RSS, post-processing |
| 4.0 | Always-on | Background service, remote control, web UI |
| 5.0+ | Ecosystem | Plugins, API, import/export from other apps |

---

## Version details

### 0.1 Skeleton
**Status: implemented.** Deviations: DI + Logging only (no Generic Host), the title bar shows no icon (window/exe icon is set), and there is no `ProcessExit` hook (aria2's `--stop-with-process` covers a killed app). Verified by 15 automated tests plus manual runs (single instance, graceful stop, hard kill, orphan cleanup). Window visuals have not been reviewed by eye yet.
- Folder structure: `Core/` (no UI code), `Aria2/` (process + RPC), `App/` (views, view models), `Tests/`.
- Dependency injection + logging (file log in `%LocalAppData%\Aria2NET\logs`).
- Download and bundle `aria2c.exe` (pinned version, SHA-256 checked) into `tools/aria2/`.
- `Aria2Process`: start hidden, generated config (`rpc-listen-port` random, `rpc-secret` random, `rpc-listen-all=false`, `--stop-with-process=<our pid>`), wait until RPC answers, stop cleanly.
- `NavigationView` shell with empty pages: Downloads, Settings, About.
- Single-instance guard (second launch focuses the first).
- Test project + GitHub Actions build.
- `.editorconfig`, `Directory.Build.props` (nullable, warnings as errors, central package versions), analyzers.
- Settings store abstraction (JSON file in `%LocalAppData%\Aria2NET\settings.json`, source-generated serializer, atomic write via temp file + rename).
- App paths service (one place that knows data, logs, temp, tools folders).
- Global exception handlers (UI thread, task scheduler, AppDomain) that log and show a friendly dialog.
- Long-path support in the app manifest (`longPathAware`), per-monitor DPI awareness.
- Custom title bar with app icon and a real app icon set (exe icon, tray icon, window icon).
- Engine status indicator in the footer (Starting / Running / Crashed / Restarting) with the aria2 version.
- Orphan cleanup on startup: if a previous crash left an `aria2c.exe` running from our folder, stop it.
- Port-in-use handling: pick another random port and retry.
- Folder `docs/` with architecture notes, so future you remembers why things are where they are.

### 0.2 First download
**Status: implemented.** Deviations: one `addUri` call per URL (a line is never treated as a mirror list yet); the filename comes from aria2 (it honors `Content-Disposition`), with the decoded URL as fallback until aria2 knows it; relative dates are not needed yet (no date column), so they move to 0.4 with history. The list is cleared while the engine is down and refilled from aria2's session after a restart. Verified by 42 automated tests (including real aria2c downloading from a local HTTP server) and a manual run that added downloads over RPC to the running app (no errors logged, clean exit). **The UI itself has not been looked at**: the screen was locked during the run, so screenshots were black. Dialog, Ctrl+V, folder picker and layout still need a human check.
- `Aria2RpcClient`: JSON-RPC over HTTP first (simplest): `addUri`, `tellActive`, `tellWaiting`, `tellStopped`, `tellStatus`, `getGlobalStat`, `getVersion`.
- Models: `DownloadItem` (gid, name, status, total, completed, speed, eta, error).
- Downloads page: a list with name, size, progress bar, speed, ETA, status.
- "Add URL" dialog: one or many URLs (one per line), destination folder, optional filename.
- Polls once per second while the window is open.
- Default download folder = the user's Downloads.
- Batched polling with `system.multicall` (one request per tick instead of three).
- Differential list updates: update existing rows in place by GID instead of rebuilding the list (no flicker, scroll position kept).
- Human-readable formatting: sizes (KiB/MiB/GiB), speed, ETA ("3m 12s"), relative dates.
- Filename detection: use the server's `Content-Disposition` name, URL-decode, fall back to the URL path.
- Add dialog validation: reject empty/invalid URLs, detect `http(s)`, `ftp`, `sftp`, `magnet:`, trim whitespace, dedupe within the paste.
- Empty state on the Downloads page ("Paste a link to start") with a big Add button.
- Global stats bar: total download speed, active count, waiting count.
- Folder picker with remembered last-used folder.
- Add via Ctrl+V anywhere on the page: pasting links opens the Add dialog pre-filled.
- Graceful handling of the engine being down: list shows a banner, Add is disabled with an explanation.
- Row context menu skeleton (filled out in 0.3).

### 0.3 Control
**Status: implemented.** Deviations: rename and change-folder are only offered for queued/paused downloads that have **no data yet** (changing them later would orphan the partial file); "added/finished" in Properties exists only for downloads this app run saw being added or finishing (real history comes with SQLite in 0.4); "final URL after redirect" is not shown because aria2 only reports the original URL (verified with a real 302); "Force start" moves the item to the top and raises `max-concurrent-downloads` by one until it finishes (aria2 has no real bypass); Move up/down is disabled while a column sort is active. Remove (keep file) is deferred 5 s so it can be undone; a removal still pending when the window closes is made final. Verified by 78 automated tests (26 new unit tests, 10 new real-aria2c tests for pause/resume, remove, delete, retry, queue moves, rename/folder/speed limit, force start) and by driving the real window through Windows UI Automation: add dialog, pause/resume, selection surviving a row move, remove + undo + finalize, delete confirmation, delete-file, clear completed, context menu, Properties, sort headers, filter tabs, Rename and Speed limit dialogs, Space/Del/Ctrl+A. **Not yet exercised in the live window:** Change folder, the "Don't ask again" box and the Settings switch, Shift+Del, Enter/double-click to open a file, and force start / queue moves from the menu (the operations behind them are covered by the automated tests). Looks (spacing, colors in light theme) were only judged from a few screenshots.
- Pause, resume, pause all, resume all.
- Remove (keep file) and Remove + delete file.
- Retry failed download.
- Open file, open containing folder, copy URL.
- Multi-select + keyboard shortcuts (Del, Space, Ctrl+A).
- Status filters: All / Downloading / Waiting / Paused / Completed / Failed.
- Friendly error messages (map aria2 error codes to text).
- Right-click context menu with everything above plus Properties.
- Command bar in the toolbar with icons and tooltips; buttons enable/disable based on selection.
- Confirmation dialogs for destructive actions, with a "don't ask again" checkbox.
- Undo toast for "Remove" (5 seconds) before the removal is final.
- Sort by any column; sort state remembered.
- Rename file before it starts (waiting/paused items).
- Change a download's folder while paused.
- Per-download speed limit from the context menu (`changeOption`).
- "Force start" for waiting items (jump the queue).
- Move up / move down / top / bottom in the queue (`changePosition`).
- Properties dialog: full URL, final URL after redirect, referer, size, path, hash, connection count, added/finished time, error text.
- Progress state colors (blue downloading, gray paused, green done, red failed) and accessible text alongside color.
- Clear completed / Clear failed buttons.

### 0.4 Persistence
- SQLite database: history of every download (URL, path, size, dates, status).
- aria2 session file (`--save-session`, `--input-file`) so unfinished downloads resume after restart or crash.
- Graceful shutdown calls `saveSession` first.
- Engine crash detection + automatic restart.
- Window size/position/column widths remembered.
- Search box over history.
- Database migrations (versioned schema, applied on startup, backed up before upgrade).
- History page separate from the live list: filter by date range, status, domain, category; sort; export to CSV/JSON.
- "Re-download" and "Download again to a different folder" from history.
- File-exists check: history shows a missing-file indicator if the file was moved/deleted.
- History retention setting (keep forever / 30 / 90 / 365 days) and a Clear history button.
- Reconcile on startup: compare aria2's results with the database so nothing is double-counted or lost.
- Detect and offer to repair a corrupted session file (keep `.bak` copies).
- Handle `.aria2` control files left by interrupted downloads (resume, or offer to clean up).
- Exclusive-start lock so two Windows sessions or two copies can't corrupt one data folder.
- Backup of the database on every successful start (keep the last N).

### 0.5 Torrents
- Open `.torrent` file (file picker, drag and drop, file association).
- Magnet links (paste into Add dialog, `magnet:` protocol handler).
- File-selection dialog before starting (tree with checkboxes, sizes).
- Torrent detail: files, peers, trackers, seeds/leeches, ratio, upload speed.
- Seeding rules: stop at ratio / time, global upload limit.
- Default public tracker list injected via `bt-tracker` (fetched, cached).
- Metalink (`.metalink`/`.meta4`) import.
- Magnet metadata phase shown clearly ("Fetching metadata…") before the file list appears.
- Save fetched metadata as a `.torrent` file (`bt-save-metadata`) so re-adding is instant.
- Separate Torrents view/filter, and a "Seeding" status distinct from "Downloading".
- Per-torrent seeding override (seed forever, seed to ratio X, don't seed).
- Peer table with country flag (offline GeoIP database), client name, speed, progress, flags.
- Tracker table with status and message; add/remove trackers on a running torrent.
- Piece/availability bar for the whole torrent.
- Pre-allocation choice for torrents (sparse vs full) shown with free-space check.
- Free disk space check before starting any download, with a warning if the file will not fit.
- Duplicate torrent detection by info hash (open the existing one instead of adding twice).
- Torrent file association via the installer / first-run prompt.
- Display of DHT status (node count) and connectability (port open or not) in the footer.
- Verify / recheck existing data ("I already have the files, check them") using `check-integrity`.
- Sequential-ish mode for streaming isn't supported by aria2 — document it as a limitation.

### 0.6 Lives in background
- Tray icon (close-to-tray, left click opens, right click menu: Pause all, Resume all, Open folder, Exit).
- Toast notifications: complete, failed, torrent finished. Click opens the file/folder.
- Start with Windows (registry Run key), option "start minimized".
- Taskbar progress (overall).
- Prevent sleep while downloading; option to pause on battery.
- "When finished: do nothing / sleep / hibernate / shut down / exit app".
- Tray icon states: idle, downloading (animated or badge with speed), error, paused.
- Tray tooltip shows current speed and active count.
- Notification settings: per event type (complete, failed, torrent done), sound on/off, "only when window is not focused", quiet hours / respect Focus Assist.
- Batch notifications ("5 downloads completed") instead of 5 toasts in a row.
- Notification action buttons: Open, Show in folder, Retry.
- Minimize behavior options: minimize to tray, close to tray, or exit.
- Confirm exit when downloads are active.
- Global hotkey to show/hide the window and to open Add dialog.
- Pause everything automatically on metered connection (opt-in).
- Resume-on-start option: auto-resume previously active downloads, or start paused.
- Relaunch-on-crash watchdog (small helper) so the tray stays alive.
- Lock-screen/session awareness: keep downloading when locked, handle sign-out gracefully.

### 0.7 Settings
- General: theme (system/light/dark), language (English only for now, resource files from day one), startup, tray behavior.
- Downloads: default folder, temp (incomplete) folder, max concurrent, connections per server, split count, min split size, retries, timeouts, user agent, file allocation, auto-rename vs overwrite vs ask.
- Speed: global download/upload limit, quick toggle "limit on/off" in the toolbar.
- Network: proxy (none/system/manual with auth), check certificates toggle, IPv6 toggle.
- Torrent: ports, DHT, PEX, LPD, encryption, seed ratio/time, tracker list source.
- Add dialog gets "More options": headers, referer, cookies, username/password, checksum, per-download limits.
- Raw aria2 options editor (key/value) for advanced users.
- Settings search box.
- Every setting has a "Reset to default" and inline help text.
- Changes that can be applied live use `changeGlobalOption`; others show "restart engine to apply" with a one-click restart.
- User-agent presets (Chrome, Firefox, Edge, aria2 default, custom).
- Settings import/export (JSON).
- Settings validation (ports in range, folders writable, limits positive) with inline errors.
- Folder write-test and free-space display for the chosen download/temp folders.
- Separate profile for "Download from this site with these headers" saved as a reusable preset.
- Log level and "Open log folder" in an Advanced section.
- Reset-all and "Open data folder" buttons.
- About page: version, aria2 version, licenses (GPL, third-party notices), links.

### 0.8 = MVP
Nothing new except stabilization. MVP definition: **you can install it, paste links/magnets/torrents, it downloads reliably, survives restarts, lives in the tray, and notifies you.** Fix everything that bites during a week of daily use.

### 0.9 Hardening
- Unit tests (RPC client, parser, models), integration tests (real aria2c against a local test server).
- Crash handling: global exception logging, "Copy diagnostics" button.
- Accessibility pass (keyboard nav, screen reader names, high contrast).
- DPI/ARM64 checks (x64 aria2 runs under emulation on ARM).
- Installer (Inno Setup) plus a portable zip.
- README with screenshots, install guide, user docs.
- Soak test: 100+ simultaneous downloads, 10 GB+ files, 1000+ history rows, a week of uptime.
- Memory/handle leak check (WinUI list virtualization, event handler unsubscription).
- Test on slow disks, network drops, sleep/resume, airplane mode, VPN toggle, full disk, read-only target folder, path > 260 chars, non-ASCII filenames.
- Verify clean uninstall (leaves user data unless asked) and upgrade-in-place keeps settings.
- Security review of the RPC secret handling, file path handling, and anything that runs external programs.
- Code signing: apply to the free SignPath Foundation OSS program (GPL-3.0 qualifies); no paid certificate, no self-signed. Until approved, ship unsigned with SHA-256 checksums and note the SmartScreen warning in the README.
- Publish to winget (free; unsigned open-source apps are accepted) via a manifest PR to `microsoft/winget-pkgs`, automated from the release workflow where possible.
- Release checklist and changelog format (`CHANGELOG.md`).
- Issue templates and a "Report a bug" button that attaches diagnostics.

---

### 1.0 Full feature release
Everything that makes it a complete IDM-class manager without a browser extension.

**Organization**
- Categories (Video, Music, Documents, Programs, Archives, Torrents, Other) with default folders and auto-assign by extension/MIME/domain. Sidebar shows categories with counts.
- Move on complete: download into the temp folder, then move to the category folder (retry if the file is locked).
- Filename templates (`{date}`, `{domain}`, `{category}`), duplicate handling (skip/rename/overwrite/ask).
- Sanitize filenames (reserved Windows names, trailing dots, path traversal).

**Queues and scheduling**
- Queue reordering (drag, move to top/bottom), priorities.
- Multiple named queues with their own concurrency and speed limit.
- Scheduler: start/stop at times, speed limits by time of day and weekday, "wake PC to start".
- Network awareness: pause on metered connection, per-Wi-Fi rules.

**Adding downloads**
- Clipboard monitor (offer to download copied links with an extension filter).
- Batch: import a text file of links, sequence generator (`file[001-100].jpg`), link checker (HEAD requests to show size/availability first).
- Drag and drop links from anywhere.
- Mirrors: add multiple URLs for one file, add/remove mirrors on a running download.
- Authentication manager: saved logins per site (Windows Credential Locker).
- Checksum verify UI, import `.sha256`/`.md5` sidecars.

**Details and visuals**
- Detail pane: files, URIs/mirrors, servers, peers, trackers, options, log.
- Piece map, speed graph (per download and global), history of bandwidth.
- Column customization, grouping, density modes, compact "mini" window, floating drop-zone widget.
- Jump list entries, taskbar overlay.

**After download**
- Auto-extract archives (7-Zip), delete archive afterwards.
- Run a program on complete (per category) with confirmation.
- Defender scan hook; preserve Mark-of-the-Web.
- Open/show file when done, optional.

**Torrents, advanced**
- Create `.torrent` files.
- Bind to a network interface (VPN-only mode), kill-switch behavior.
- Per-torrent limits, force-start, recheck, change trackers.

**Polish**
- Backup/restore all settings, categories, queues.
- Update checker (app + aria2 binary).
- Localization infrastructure, first extra languages.
- Onboarding (TeachingTips), keyboard shortcut reference, in-app help.

**More 1.0 items**
- Per-site profiles: saved headers, cookies, referer, login, connection count, and speed limit that apply automatically to a domain.
- Cookie import from a `cookies.txt` file (for sites you can't capture yet, before 2.0).
- "Copy as aria2c command" and "Copy as curl" for any download (debug and share).
- Proxy pool / per-category proxy; test-proxy button.
- Speed limit presets ("Full speed", "50%", "Background 1 MB/s") on the tray and toolbar.
- "Turbo / fair share" modes: give the selected download priority bandwidth.
- Bandwidth usage meter with monthly cap warning (for metered plans).
- Duplicate detection across history (same URL, same hash, same name+size) with a prompt.
- Partial-file recovery: adopt a half-downloaded file from the browser or another tool.
- Resume after URL expiry: refresh a dead link ("Update URL" keeps the partial data).
- File preview while downloading (video partial play via external player, image thumbnails for finished files).
- Thumbnail/icon per file type, and file-type icons in the list from the shell.
- Disk space guard: pause all when free space < N GB, resume when freed.
- Library view of completed files per category with Open / Show / Share / Delete to Recycle Bin.
- Safe delete: send to Recycle Bin instead of permanent delete.
- Command palette (Ctrl+K) to run any action.
- Multiple windows / compact "overlay" window pinned on top.
- Fully customizable keyboard shortcuts.
- Light/dark/high-contrast/accent-color themes; Mica / Acrylic options.
- Export log and diagnostics bundle (zip) with secrets redacted.
- Portable mode (data stored next to the exe via a marker file).
- Command-line interface: `Aria2NET.exe add <url>`, `--minimized`, `--pause-all`, handled by the running instance.
- Full unit/UI test coverage of critical flows, performance budget (cold start < 1.5 s, idle CPU ~0%, idle RAM under a set budget).

### 1.x ideas (small releases after 1.0)
- Import from Motrix/AriaNg/IDM/uTorrent/qBittorrent lists.
- Share target + "Send to Aria2NET" context menu for files/links.
- QR code/LAN receive page.
- Site-specific tuning (some hosts throttle per connection, tune split per host).
- Statistics page (total downloaded, per-day charts).
- Auto-tune: measure which connection count gives the best speed per host and remember it.
- Windows Widgets / Start menu live tile with current speed.
- Explorer integration: "Download with Aria2NET" for `.torrent`, `.metalink`, `.txt` link lists.
- Drag a file out of the app into Explorer or another app.
- Notifications center inside the app (history of events).
- Tray-only "quick add" flyout (paste, press Enter).
- Download speed test (check your line vs server).
- Windows Search / jump to file from within the app.
- Telemetry-free "what's new" screen after updates.
- Voice/Narrator-friendly status announcements for completions.

---

### 2.0 Browser capture
This is the "no more copy link, paste, fail" release.
- Chrome/Edge extension (Manifest V3) that cancels the browser's download and sends it to Aria2NET with URL, referer, cookies, user agent, headers, filename, size.
- Talks to the app through a small local endpoint (127.0.0.1, token-protected, Origin-checked) or native messaging (decide at start of 2.0; with an unpackaged app both are straightforward).
- If Aria2NET isn't running, the extension can launch it, or falls back to the browser downloading normally. A download is never lost.
- Capture rules: by file extension, size, domain, MIME. Alt-click bypass. Per-site toggle.
- Context menu: Download with Aria2NET, Download all links, Download selected links, Download all images.
- Popup UI: connection status, enable/disable, recent captures.
- Firefox version.
- Extension store listings (Chrome Web Store, Edge Add-ons, Firefox AMO) plus sideload instructions.
- App side: "Browser integration" settings page, install helper (writes the registration), connection log.

Known limits to be honest about: form-POST downloads can't be replayed by aria2 (browser keeps them), some sites reject non-browser clients, one-time URLs must be forwarded instantly.

**More 2.0 items**
- Pairing: the extension and the app exchange a one-time code on first connect, so random local software can't impersonate the extension.
- Connection state indicator in the extension icon (green connected, gray app closed, red error).
- Extension capture of the *final* filename and size from response headers, including redirect chains.
- Ask-before-download popup (choose folder, rename, category, "download later") with an "always do this" checkbox.
- Capture blocklist by default for sites that break (banking, Google Drive web viewer, sites requiring POST).
- Handling of blob: and data: URLs (let the browser keep them, or save via the extension).
- Incognito / private-window support with explicit opt-in; never store those URLs in history unless chosen.
- Per-browser profile support (multiple Chrome profiles → one app).
- Sync of capture rules between app and extension.
- Download "queue to Aria2NET" button on pages that list multiple files.
- Handle `Content-Disposition: attachment` and sniffed MIME types for downloads with no extension.
- Enterprise-friendly: extension and native messaging manifest documented for admins; works with Chrome policies.
- Uninstall helper removes the registration cleanly.
- Telemetry-free diagnostics page in the extension ("test connection", last 20 captures).

### 2.x
- Media sniffer: detect video/audio on pages, floating "download" button.
- HLS/DASH via bundled ffmpeg or yt-dlp.
- Brave/Opera/Vivaldi support.
- Page grabber: all PDFs/images/links of a page with filters.
- Quality picker for detected media (resolution, bitrate, audio-only).
- Subtitle and thumbnail download alongside video.
- Playlist/channel batch download via yt-dlp.
- Auto-merge audio+video streams with ffmpeg; progress reported in the same list.
- yt-dlp auto-updater.
- Cookie-from-browser option for yt-dlp (for sites needing login).
- "Send to Aria2NET" from Firefox's built-in download panel, Edge's downloads page, and Safari/Opera if feasible.
- Right-click on a torrent/magnet link on any page.
- Drag a link from the browser onto the tray icon or floating drop zone.
- Smart category detection from page context (e.g. a PDF on a university site → Documents).
- Resume interrupted browser downloads (take over a failed Chrome download by its URL).
- Optional "takeover" of downloads finished by the browser: move/organize them like ours.

---

### 3.0 Automation
- Rules engine: if (domain, type, size, category, time) then (category, folder, speed limit, queue, run script, notify).
- RSS feeds with filters (auto-download new items, torrent episode trackers).
- Site grabber/spider with depth and filters.
- Post-processing pipelines (extract, rename, move, scan, notify, run) as configurable steps.
- Scripting hooks (PowerShell scripts as event handlers).
- Retry policies: re-queue failed downloads on a schedule, switch mirror, switch to browser fallback.
- Conditional logic in rules (AND/OR/NOT), rule ordering, rule testing ("what would happen to this URL?"), rule import/export and sharing.
- Triggers beyond "on add": on complete, on fail, on schedule, on network change, on idle, on disk space low, on file appears in a watch folder.
- Watch folder: drop a `.torrent`, `.metalink`, or `.txt` of links into a folder and it is added automatically.
- Smart renaming: regex, media-aware (title/season/episode from names), strip junk (`[site.com]`), date from metadata.
- Duplicate-aware sorting: keep best quality, skip existing library items.
- Media library hand-off: notify Plex/Jellyfin/Emby to rescan; move into media folders with correct names.
- Webhooks on events (Discord, Slack, Telegram, ntfy, generic HTTP).
- Email/push notifications for long-running or overnight downloads.
- Per-rule bandwidth schedules ("this domain only at night").
- Seeding automation: after seeding goal met, move/delete/hardlink and remove the torrent.
- Tracker-specific rules (ratio targets per tracker).
- Download dependency chains (download A, then B, then extract C).
- Variables and templates available in every field (`{host}`, `{date}`, `{rule}`, `{category}`, `{size}`).
- Dry-run mode and an audit log of every automated action with undo where possible.
- Script runner: PowerShell/Python/exe hooks with timeout, sandboxed working directory, environment variables describing the download.
- Link-list feeds: poll a URL for new links (blogs, release pages) and queue them.
- Scheduled one-time and recurring jobs (e.g. "every Friday 02:00 mirror this directory listing").
- Directory-listing mirroring (like `wget -m`): recursive HTTP/FTP/SFTP folders with include/exclude filters and incremental sync.
- Conditional re-download when the server file changes (ETag/Last-Modified).

### 4.0 Always-on
- Split into a background host (optionally a Windows Service) and the UI as a client; downloads run with no window and before login.
- Remote control: authenticated TLS web UI, mobile-friendly.
- Connect the UI to a remote aria2 (NAS, seedbox) via RPC URL + secret; multiple engine profiles.
- Multi-user considerations.
- UI auto-reconnect, "engine is somewhere else" status, latency display.
- Per-profile history and settings; switch engines from the title bar.
- Engine-side scheduling so rules still work when the UI is closed.
- Secure pairing flow for remote clients (QR code or code entry), device list with revoke.
- Role-based access (view-only link vs full control), read-only dashboards.
- Let's Encrypt / self-signed cert helper, or Tailscale/Cloudflare tunnel guide for safe remote access without opening ports.
- Command-line client (`a2n add`, `a2n list`) talking to the host.
- Docker image / Linux headless host for the same engine + rules (web UI only).
- Health monitoring, auto-heal, update-in-place for the host.
- Event stream (WebSocket) so any client sees live changes.
- Mobile companion app (or PWA) with push notifications.
- Disk and bandwidth quotas per profile or user.

### 5.0+ Ecosystem
- Local REST/WebSocket API for third-party tools (Stream Deck, PowerToys Run).
- Plugin system (site extractors, post-processors).
- Cloud upload after download (S3, SFTP, WebDAV, OneDrive).
- Theming, community translations.
- Plugin sandboxing, permissions model, signed plugins, in-app plugin browser.
- Official SDK (NuGet) and sample plugins; documentation site.
- Site extractors for hosting sites that need special handling (resolve to direct links).
- Cloud-to-cloud transfers (e.g. remote URL → your storage without touching your PC).
- Debrid/premium link service integrations (optional, user-provided API keys).
- Usenet (NZB) support via an extra backend, if wanted — aria2 doesn't do it.
- P2P extras: web-seed/HTTP-fallback preferences, Tor/VPN-aware profiles.
- Integrations: Home Assistant, Sonarr/Radarr-style hand-off, Raycast/PowerToys Run, Alfred-like launchers.
- Mod/theme gallery and a community translations platform.
- Telemetry-free anonymous opt-in stats ("which options are most used") if ever needed.
- Long-term: ports (Linux via Avalonia/Uno, macOS) sharing `Core/` and `Aria2/` libraries — this is why those folders have no UI code in 0.1.

---

## Reference: what aria2 gives us (so you know what's free)

- **Protocols:** HTTP, HTTPS, FTP, SFTP, BitTorrent, Metalink.
- **Transfer:** multi-connection per file (`-x`, `-s`, `-k`), resume, retries, mirrors, queue with concurrency, global and per-download speed limits, checksum verification, file allocation modes, disk cache, conditional get, remote timestamps, auto-rename.
- **HTTP options:** headers, referer, user agent, cookies (Netscape file, Firefox/Chrome sqlite), basic/digest auth, netrc, proxies per protocol, TLS options and client certs.
- **BitTorrent:** DHT, PEX, LPD, encryption, magnet + metadata fetch, selective files, seeding ratio/time, trackers lists and UDP trackers, WebSeed, peer limits, listen ports.
- **RPC (what we use):** `addUri`, `addTorrent`, `addMetalink`, `remove`, `forceRemove`, `pause`, `forcePause`, `pauseAll`, `unpause`, `unpauseAll`, `tellStatus`, `tellActive`, `tellWaiting`, `tellStopped`, `getUris`, `getFiles`, `getPeers`, `getServers`, `changePosition`, `changeUri`, `getOption`, `changeOption`, `getGlobalOption`, `changeGlobalOption`, `getGlobalStat`, `purgeDownloadResult`, `removeDownloadResult`, `getVersion`, `getSessionInfo`, `shutdown`, `saveSession`, `system.multicall`. WebSocket notifications: `onDownloadStart/Pause/Stop/Complete/Error`, `onBtDownloadComplete`.
- **Not in aria2 (we build it):** GUI, scheduler, categories, history, browser capture, tray/notifications, HLS/video sites, torrent creation, RSS, tracker list updates, antivirus, post-processing. No official Windows ARM64 build (x64 under emulation); last release 1.37.0.

## Tricks and gotchas (the "more tricks" list)

1. **Cookies and referer are why pasted links fail.** The browser has them, aria2 doesn't. v2.0 solves it by forwarding them. Until then, the Add dialog's "More options" lets you paste them manually.
2. **Never read the browser's cookie database.** Chrome encrypts it in a way other programs can't read. The extension must send cookies.
3. **aria2 only does GET.** Form-POST downloads can't be captured; let the browser keep those.
4. **One-time/signed URLs:** cancel the browser's download first, then start ours immediately.
5. **Security:** RPC must stay on `127.0.0.1` with a random secret. A random local web page must not be able to talk to it.
6. **Filenames:** sanitize before writing (`CON`, `NUL`, trailing dots, `..`).
7. **Mark-of-the-Web:** keep the `Zone.Identifier` stream so SmartScreen/Office protections still apply to downloaded files.
8. **Temp folder + move on complete:** aria2 writes `file.ext` and `file.ext.aria2`. Only move after `onDownloadComplete`, and retry if an antivirus holds the file.
9. **File allocation:** `falloc` is fast on NTFS, `prealloc` is slower but avoids fragmentation; default to `falloc`.
10. **Connections:** too many connections per server (`-x 16`) gets some hosts to throttle or ban you. Default 8, let rules override per host.
11. **Disk cache:** set `disk-cache` to 16–64 MB for fast links to reduce disk writes.
12. **WinUI 3 has no tray icon built in:** use `H.NotifyIcon.WinUI` or a small Win32 wrapper.
13. **Virtualized lists:** use `ListView`/`ItemsRepeater` with virtualization; thousands of history rows will otherwise lag.
14. **Polling vs events:** poll only while the window is visible. Use WebSocket notifications in the background.
15. **aria2 shuts down with us:** `--stop-with-process` prevents orphaned aria2 processes if the app crashes.
16. **Run from Program Files = no write access:** keep data in `%LocalAppData%\Aria2NET`, never next to the exe.
17. **Torrent legality:** ship no search/indexers that point at specific infringing content.
18. **Long paths:** enable long-path support in the app manifest (`longPathAware`).
19. **Antivirus false positives:** bundling aria2c.exe can trigger them; ship hashes, build reproducibly, consider code signing later.
