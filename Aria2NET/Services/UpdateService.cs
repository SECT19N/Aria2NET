using Aria2NET.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using Velopack;
using Velopack.Sources;

namespace Aria2NET.Services;

public enum UpdateState {
	/// <summary>Not installed with Setup.exe (portable zip, a build from source): there is nothing to update.</summary>
	NotInstalled,
	Idle,
	Checking,
	UpToDate,
	Downloading,
	/// <summary>Downloaded; installing it needs a restart.</summary>
	Ready,
	Failed,
}

/// <summary>
/// Looks for new versions in the GitHub releases of the project and downloads them. Velopack does the rest (the installer, the file swap).
/// The installed copy knows its own channel (win-x64 or win-arm64), so it only ever sees packages for its own processor.
/// </summary>
public sealed class UpdateService(SettingsStore<AppSettings> settings, NoticeCenter notices, ILogger<UpdateService> logger) {
	private const string Repository = "https://github.com/SECT19N/Aria2NET";
	private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(30);
	private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(12);

	private readonly SemaphoreSlim _busy = new(1, 1);
	private UpdateManager? _manager;
	private VelopackAsset? _ready;

	public UpdateState State { get; private set; } = UpdateState.Idle;

	/// <summary>The version that is downloading or ready, once known.</summary>
	public string? NewVersion { get; private set; }

	/// <summary>Why the last check failed, for the Settings page.</summary>
	public string? Error { get; private set; }

	public event EventHandler? Changed;

	/// <summary>Set by the main window: confirms with the user when downloads are running, then exits so the update can be installed.</summary>
	public Func<Task>? RestartRequested { get; set; }

	/// <summary>True when this copy was installed by Setup.exe and can therefore update itself.</summary>
	public bool IsInstalled => (_manager ??= new UpdateManager(new GithubSource(Repository, null, true))).IsInstalled;

	/// <summary>Starts the background checks: one shortly after start, then every twelve hours while the setting is on.</summary>
	public void StartAutoCheck() {
		if (!IsInstalled) {
			State = UpdateState.NotInstalled;
			return;
		}
		_ = Task.Run(async () => {
			await Task.Delay(FirstCheckDelay);
			while (true) {
				if (settings.Load().AutoCheckUpdates && State is not (UpdateState.Ready or UpdateState.Downloading)) {
					await CheckAsync(manual: false);
				}
				await Task.Delay(CheckInterval);
			}
		});
	}

	/// <summary>Looks for a newer version and downloads it. Does nothing when a check is already running.</summary>
	public async Task CheckAsync(bool manual) {
		if (!IsInstalled) {
			Set(UpdateState.NotInstalled);
			return;
		}
		if (!await _busy.WaitAsync(0)) {
			return;
		}
		try {
			Error = null;
			Set(UpdateState.Checking);
			// The setting can change between checks, so the source is rebuilt each time.
			_manager = new UpdateManager(new GithubSource(Repository, null, settings.Load().IncludePreReleases));
			var update = await _manager.CheckForUpdatesAsync();
			if (update is null) {
				Set(UpdateState.UpToDate);
				return;
			}

			NewVersion = update.TargetFullRelease.Version.ToString();
			logger.LogInformation("Update {Version} found, downloading", NewVersion);
			Set(UpdateState.Downloading);
			await _manager.DownloadUpdatesAsync(update);
			_ready = update.TargetFullRelease;
			Set(UpdateState.Ready);
			notices.Post(new Notice(
				Loc.S("Update_ReadyTitle", "Update ready"),
				Loc.F("Update_ReadyMessage", "Aria2NET {0} is downloaded. Restart to install it; your downloads continue afterwards.", NewVersion),
				InfoBarSeverity.Success,
				new NoticeAction(Loc.S("Update_Restart", "Restart to update"), RestartAsync),
				new NoticeAction(Loc.S("Update_Later", "Later"), () => Task.CompletedTask)));
		} catch (Exception ex) {
			logger.LogWarning(ex, "Update check failed");
			Error = ex.Message;
			Set(UpdateState.Failed);
			if (manual) {
				notices.Post(new Notice(Loc.S("Update_FailedTitle", "Could not check for updates"), ex.Message, InfoBarSeverity.Warning));
			}
		} finally {
			_busy.Release();
		}
	}

	/// <summary>Asks the main window to exit; it calls <see cref="ApplyOnExit"/> once the user agreed and downloads are stopped.</summary>
	public Task RestartAsync() => RestartRequested?.Invoke() ?? Task.CompletedTask;

	/// <summary>
	/// Hands the downloaded version to Velopack's updater, which waits for this process to end, swaps the files and starts the new version.
	/// The caller must exit right after, through the normal shutdown that stops aria2 (an aria2c.exe that is still running locks its files).
	/// </summary>
	public bool ApplyOnExit() {
		if (_ready is null || _manager is null) {
			return false;
		}
		try {
			_manager.WaitExitThenApplyUpdates(_ready, silent: false, restart: true);
			return true;
		} catch (Exception ex) {
			logger.LogError(ex, "Could not start the updater");
			Error = ex.Message;
			Set(UpdateState.Failed);
			return false;
		}
	}

	private void Set(UpdateState state) {
		State = state;
		Changed?.Invoke(this, EventArgs.Empty);
	}
}
