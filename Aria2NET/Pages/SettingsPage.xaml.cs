using Aria2NET.Core;
using Aria2NET.Core.Aria2;
using Aria2NET.Core.Background;
using Aria2NET.Core.Settings;
using Aria2NET.Core.Torrents;
using Aria2NET.Services;
using Aria2NET.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace Aria2NET.Pages;

public sealed partial class SettingsPage : Page {
	public SettingsViewModel ViewModel { get; } = App.Services.GetRequiredService<SettingsViewModel>();

	public SettingsPage() {
		InitializeComponent();
		ConfirmDeleteBox.IsChecked = ViewModel.ConfirmDeleteFiles;
		CloseToTrayBox.IsChecked = ViewModel.CloseToTray;
		MinimizeToTrayBox.IsChecked = ViewModel.MinimizeToTray;
		ConfirmExitBox.IsChecked = ViewModel.ConfirmExitWithActive;
		StartWithWindowsBox.IsChecked = ViewModel.StartWithWindows;
		StartMinimizedBox.IsChecked = ViewModel.StartMinimized;
		StartMinimizedBox.IsEnabled = StartWithWindowsBox.IsChecked == true;
		StartPausedBox.IsChecked = ViewModel.StartPaused;
		NotifyCompleteBox.IsChecked = ViewModel.NotifyOnComplete;
		NotifyFailedBox.IsChecked = ViewModel.NotifyOnFailed;
		NotifyTorrentBox.IsChecked = ViewModel.NotifyOnTorrentFinished;
		NotifySoundBox.IsChecked = ViewModel.NotifySound;
		NotifyUnfocusedBox.IsChecked = ViewModel.NotifyOnlyWhenUnfocused;
		PreventSleepBox.IsChecked = ViewModel.PreventSleep;
		PauseBatteryBox.IsChecked = ViewModel.PauseOnBattery;
		PauseMeteredBox.IsChecked = ViewModel.PauseOnMetered;
		HotkeysBox.IsChecked = ViewModel.HotkeysEnabled;
		RestartBox.IsChecked = ViewModel.RestartAfterCrash;
		foreach (var action in Enum.GetValues<FinishAction>()) {
			FinishBox.Items.Add(WhenFinishedMachine.Label(action));
		}
		FinishBox.SelectedIndex = (int)FinishService.Action;
		foreach (var choice in SettingsViewModel.RetentionChoices) {
			RetentionBox.Items.Add(choice.Label);
		}
		RetentionBox.SelectedIndex = ViewModel.RetentionIndex;
		RetentionBox.IsEnabled = ViewModel.HistoryAvailable;
		RefreshAssociation();
		TrackerStatus.Text = TrackerSummary();
		foreach (var (label, _) in SettingsViewModel.UserAgentChoices) {
			UserAgentBox.Items.Add(label);
		}
		foreach (var mode in Enum.GetValues<FileAllocationMode>()) {
			AllocationBox.Items.Add(mode.ToString());
		}
		LoadEngineSettings();
		var applier = App.Services.GetRequiredService<SettingsApplier>();
		EventHandler onSpeed = (_, _) => DispatcherQueue.TryEnqueue(LoadEngineSettings);
		Loaded += (_, _) => applier.SpeedChanged += onSpeed;
		Unloaded += (_, _) => applier.SpeedChanged -= onSpeed;
	}

	// ---------------------------------------------------------------- downloads and speed

	private bool _loading;

	private void LoadEngineSettings() {
		_loading = true;
		try {
			var s = ViewModel.Current;
			FolderBox.Text = s.DefaultDownloadFolder ?? "";
			ConcurrentBox.Value = s.MaxConcurrentDownloads;
			ConnectionsBox.Value = s.MaxConnectionsPerServer;
			SplitBox.Value = s.Split;
			MinSplitBox.Value = s.MinSplitSizeMiB;
			TriesBox.Value = s.MaxTries;
			RetryWaitBox.Value = s.RetryWaitSeconds;
			ConnectTimeoutBox.Value = s.ConnectTimeoutSeconds;
			TimeoutBox.Value = s.TimeoutSeconds;
			var presets = SettingsViewModel.UserAgentChoices;
			var index = string.IsNullOrWhiteSpace(s.UserAgent) ? 0 : presets.ToList().FindIndex(c => c.Value == s.UserAgent);
			if (index < 0) {
				index = presets.Count - 1; // custom
			}
			UserAgentBox.SelectedIndex = index;
			UserAgentText.Visibility = index == presets.Count - 1 ? Visibility.Visible : Visibility.Collapsed;
			UserAgentText.Text = index == presets.Count - 1 ? s.UserAgent ?? "" : "";
			AllocationBox.SelectedIndex = (int)s.FileAllocation;
			DownLimitBox.Value = s.DownloadLimitKiBps;
			UpLimitBox.Value = s.UploadLimitKiBps;
			AltDownLimitBox.Value = s.AltDownloadLimitKiBps;
			AltUpLimitBox.Value = s.AltUploadLimitKiBps;
			AltSpeedSwitch.IsOn = s.AltSpeedActive;
			RestartBar.IsOpen = ViewModel.RestartRequired;
		}
		finally {
			_loading = false;
		}
		_ = RefreshFolderStatusAsync();
	}

	private static int Int(NumberBox box) => (int)Math.Round(box.Value);

	private void Number_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args) {
		if (_loading) {
			return;
		}
		if (double.IsNaN(sender.Value)) {
			sender.Value = double.IsNaN(args.OldValue) ? sender.Minimum : args.OldValue; // empty or invalid text: keep the old value
			return;
		}
		ViewModel.Update(s => {
			s.MaxConcurrentDownloads = Int(ConcurrentBox);
			s.MaxConnectionsPerServer = Int(ConnectionsBox);
			s.Split = Int(SplitBox);
			s.MinSplitSizeMiB = Int(MinSplitBox);
			s.MaxTries = Int(TriesBox);
			s.RetryWaitSeconds = Int(RetryWaitBox);
			s.ConnectTimeoutSeconds = Int(ConnectTimeoutBox);
			s.TimeoutSeconds = Int(TimeoutBox);
			s.DownloadLimitKiBps = Int(DownLimitBox);
			s.UploadLimitKiBps = Int(UpLimitBox);
			s.AltDownloadLimitKiBps = Int(AltDownLimitBox);
			s.AltUploadLimitKiBps = Int(AltUpLimitBox);
		});
	}

	private void UserAgent_Changed(object sender, SelectionChangedEventArgs e) {
		if (_loading || UserAgentBox.SelectedIndex < 0) {
			return;
		}
		var choice = SettingsViewModel.UserAgentChoices[UserAgentBox.SelectedIndex];
		var custom = UserAgentBox.SelectedIndex == SettingsViewModel.UserAgentChoices.Count - 1;
		UserAgentText.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
		if (!custom) {
			ViewModel.Update(s => s.UserAgent = choice.Value);
		}
	}

	private void UserAgentText_LostFocus(object sender, RoutedEventArgs e) {
		if (!_loading) {
			ViewModel.Update(s => s.UserAgent = UserAgentText.Text.Trim());
		}
	}

	private void Allocation_Changed(object sender, SelectionChangedEventArgs e) {
		if (!_loading && AllocationBox.SelectedIndex >= 0) {
			ViewModel.Update(s => s.FileAllocation = (FileAllocationMode)AllocationBox.SelectedIndex);
		}
	}

	private void AltSpeed_Toggled(object sender, RoutedEventArgs e) {
		if (!_loading) {
			_ = ViewModel.SetAltSpeedAsync(AltSpeedSwitch.IsOn);
		}
	}

	private void ResetDownloads_Click(object sender, RoutedEventArgs e) {
		ViewModel.ResetDownloads();
		LoadEngineSettings();
	}

	private void ResetSpeed_Click(object sender, RoutedEventArgs e) {
		ViewModel.ResetSpeed();
		LoadEngineSettings();
	}

	private async void BrowseFolder_Click(object sender, RoutedEventArgs e) {
		var picker = new Windows.Storage.Pickers.FolderPicker();
		picker.FileTypeFilter.Add("*");
		WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.Window));
		if (await picker.PickSingleFolderAsync() is { } folder) {
			FolderBox.Text = folder.Path;
			await CommitFolderAsync();
		}
	}

	private async void Folder_LostFocus(object sender, RoutedEventArgs e) => await CommitFolderAsync();

	private async Task CommitFolderAsync() {
		var path = FolderBox.Text.Trim();
		if (_loading || path == (ViewModel.Current.DefaultDownloadFolder ?? "")) {
			return;
		}
		if (path.Length > 0 && await Task.Run(() => SettingsViewModel.CheckFolder(path)) is { } problem) {
			FolderStatus.Text = problem;
			return; // not saved
		}
		ViewModel.Update(s => s.DefaultDownloadFolder = path.Length == 0 ? null : path);
		RestartBar.IsOpen = ViewModel.RestartRequired;
		await RefreshFolderStatusAsync();
	}

	private async Task RefreshFolderStatusAsync() {
		var folder = DefaultFolderResolver.ForEngine(ViewModel.Current);
		FolderStatus.Text = await Task.Run(() => SettingsViewModel.FreeSpaceText(folder));
	}

	private async void RestartEngine_Click(object sender, RoutedEventArgs e) {
		RestartBar.IsOpen = false;
		await ViewModel.RestartEngineAsync();
		RestartBar.IsOpen = ViewModel.RestartRequired;
	}

	private void Retention_Changed(object sender, SelectionChangedEventArgs e) {
		if (RetentionBox.SelectedIndex >= 0 && RetentionBox.SelectedIndex != ViewModel.RetentionIndex) {
			ViewModel.RetentionIndex = RetentionBox.SelectedIndex;
		}
	}

	private async void ClearHistory_Click(object sender, RoutedEventArgs e) {
		if (await App.Window.ConfirmAsync("Clear all history?", "Every finished, failed and removed entry is forgotten. Downloads still in progress stay. Downloaded files are not deleted.", "Clear history")) {
			await Task.Run(ViewModel.ClearHistory);
		}
	}

	private void ConfirmDelete_Click(object sender, RoutedEventArgs e) => ViewModel.ConfirmDeleteFiles = ConfirmDeleteBox.IsChecked == true;

	private void CloseToTray_Click(object sender, RoutedEventArgs e) => ViewModel.CloseToTray = CloseToTrayBox.IsChecked == true;

	private void MinimizeToTray_Click(object sender, RoutedEventArgs e) => ViewModel.MinimizeToTray = MinimizeToTrayBox.IsChecked == true;

	private void ConfirmExit_Click(object sender, RoutedEventArgs e) => ViewModel.ConfirmExitWithActive = ConfirmExitBox.IsChecked == true;

	private void StartWithWindows_Click(object sender, RoutedEventArgs e) {
		ViewModel.StartWithWindows = StartWithWindowsBox.IsChecked == true;
		StartWithWindowsBox.IsChecked = ViewModel.StartWithWindows; // show what actually happened if the registry refused
		StartMinimizedBox.IsEnabled = StartWithWindowsBox.IsChecked == true;
	}

	private void StartMinimized_Click(object sender, RoutedEventArgs e) => ViewModel.StartMinimized = StartMinimizedBox.IsChecked == true;

	private void StartPaused_Click(object sender, RoutedEventArgs e) => ViewModel.StartPaused = StartPausedBox.IsChecked == true;

	private void NotifyComplete_Click(object sender, RoutedEventArgs e) => ViewModel.NotifyOnComplete = NotifyCompleteBox.IsChecked == true;

	private void NotifyFailed_Click(object sender, RoutedEventArgs e) => ViewModel.NotifyOnFailed = NotifyFailedBox.IsChecked == true;

	private void NotifyTorrent_Click(object sender, RoutedEventArgs e) => ViewModel.NotifyOnTorrentFinished = NotifyTorrentBox.IsChecked == true;

	private void NotifySound_Click(object sender, RoutedEventArgs e) => ViewModel.NotifySound = NotifySoundBox.IsChecked == true;

	private void NotifyUnfocused_Click(object sender, RoutedEventArgs e) => ViewModel.NotifyOnlyWhenUnfocused = NotifyUnfocusedBox.IsChecked == true;

	private static WhenFinishedService FinishService => App.Services.GetRequiredService<WhenFinishedService>();

	private void PreventSleep_Click(object sender, RoutedEventArgs e) => ViewModel.PreventSleep = PreventSleepBox.IsChecked == true;

	private void PauseBattery_Click(object sender, RoutedEventArgs e) => ViewModel.PauseOnBattery = PauseBatteryBox.IsChecked == true;

	private void PauseMetered_Click(object sender, RoutedEventArgs e) => ViewModel.PauseOnMetered = PauseMeteredBox.IsChecked == true;

	private void Finish_Changed(object sender, SelectionChangedEventArgs e) {
		if (FinishBox.SelectedIndex >= 0 && FinishBox.SelectedIndex != (int)FinishService.Action) {
			FinishService.Action = (FinishAction)FinishBox.SelectedIndex;
		}
	}

	private void Hotkeys_Click(object sender, RoutedEventArgs e) {
		ViewModel.HotkeysEnabled = HotkeysBox.IsChecked == true;
		var free = App.Services.GetRequiredService<BackgroundCoordinator>().ApplyHotkeys();
		HotkeysStatus.Text = free ? "" : "Another program already uses one of these shortcuts, so it is not active.";
	}

	private void Restart_Click(object sender, RoutedEventArgs e) {
		ViewModel.RestartAfterCrash = RestartBox.IsChecked == true;
		App.Services.GetRequiredService<BackgroundCoordinator>().ApplyCrashRestart();
	}

	private async void OpenData_Click(object sender, RoutedEventArgs e) => await Launcher.LaunchFolderPathAsync(ViewModel.DataFolder);

	private async void OpenLogs_Click(object sender, RoutedEventArgs e) => await Launcher.LaunchFolderPathAsync(ViewModel.LogsFolder);

	// ---------------------------------------------------------------- torrents

	private static TorrentAssociation Association => App.Services.GetRequiredService<TorrentAssociation>();

	private void RefreshAssociation() {
		var association = Association;
		AssociationBox.IsEnabled = association.IsAvailable;
		var state = association.State;
		AssociationBox.IsChecked = state.Registered;
		DefaultAppsLink.Visibility = state.Registered && !(state.DefaultForTorrent && state.DefaultForMagnet) ? Visibility.Visible : Visibility.Collapsed;
		AssociationStatus.Text = !state.Registered
			? ""
			: state.DefaultForTorrent && state.DefaultForMagnet
				? "Aria2NET is the default app for .torrent files and magnet links."
				: "Aria2NET is offered to Windows. Windows lets only you choose the default app: pick Aria2NET for .torrent files and for the MAGNET link type.";
	}

	private async void Association_Click(object sender, RoutedEventArgs e) {
		if (AssociationBox.IsChecked == true) {
			if (!Association.Enable()) {
				RefreshAssociation();
				await TorrentAssociation.OpenDefaultAppsAsync();
			}
		}
		else {
			Association.Disable();
		}
		RefreshAssociation();
	}

	private async void DefaultApps_Click(object sender, RoutedEventArgs e) => await TorrentAssociation.OpenDefaultAppsAsync();

	private static string TrackerSummary() {
		var paths = App.Services.GetRequiredService<AppPaths>();
		var count = TrackerListService.ReadCache(paths).Count;
		return count == 0 ? "No list downloaded yet." : $"{count} trackers cached; refreshed automatically every week.";
	}

	private async void UpdateTrackers_Click(object sender, RoutedEventArgs e) {
		TrackerStatus.Text = "Downloading…";
		var list = await App.Services.GetRequiredService<TrackerListService>().RefreshAsync();
		if (list is not null && App.Services.GetRequiredService<IAria2Engine>().Rpc is { } rpc) {
			try {
				await rpc.ChangeGlobalOptionAsync(new Dictionary<string, string> { ["bt-tracker"] = Aria2Config.TrackerOptionValue(list) });
			}
			catch (Exception ex) when (ex is Aria2RpcException or HttpRequestException) {
				// The new list is cached and used at the next engine start.
			}
		}
		TrackerStatus.Text = list is null ? "Could not download the list; keeping the old one." : TrackerSummary();
	}
}
