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
		FinishBox.SelectedIndex = (int) FinishService.Action;
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
		foreach (var mode in new[] { Loc.S("Settings_ProxyNone", "No proxy"), Loc.S("Settings_ProxySystem", "Use the Windows proxy"), Loc.S("Settings_ProxyManual", "Manual") }) {
			ProxyModeBox.Items.Add(mode);
		}
		foreach (var theme in new[] { Loc.S("Settings_ThemeSystem", "Use the Windows setting"), Loc.S("Settings_ThemeLight", "Light"), Loc.S("Settings_ThemeDark", "Dark") }) {
			ThemeBox.Items.Add(theme);
		}
		foreach (var policy in new[] { Loc.S("Settings_ConflictRename", "Keep both (rename the new one)"), Loc.S("Settings_ConflictOverwrite", "Replace the existing file"), Loc.S("Settings_ConflictAsk", "Ask me") }) {
			ConflictBox.Items.Add(policy);
		}
		LanguageBox.Items.Add("English");
		LanguageBox.SelectedIndex = 0;
		foreach (var source in new[] { Loc.S("Settings_TrackersDefault", "Public list (built in)"), Loc.S("Settings_TrackersCustom", "Custom list address"), Loc.S("Settings_TrackersOff", "Off (only the torrent's own trackers)") }) {
			TrackerSourceBox.Items.Add(source);
		}
		foreach (var encryption in new[] { Loc.S("Settings_EncAllow", "Allow (encrypted or plain)"), Loc.S("Settings_EncPrefer", "Prefer encrypted"), Loc.S("Settings_EncRequire", "Require encrypted (fewer peers)") }) {
			EncryptionBox.Items.Add(encryption);
		}
		foreach (var level in new[] { Loc.S("Settings_LogWarnings", "Warnings only"), Loc.S("Settings_LogNormal", "Normal"), Loc.S("Settings_LogDebug", "Debug (very detailed)") }) {
			LogLevelBox.Items.Add(level);
		}
		LoadEngineSettings();
		BuildSections();
		var applier = App.Services.GetRequiredService<SettingsApplier>();
		EventHandler onSpeed = (_, _) => DispatcherQueue.TryEnqueue(LoadEngineSettings);
		Loaded += (_, _) => applier.SpeedChanged += onSpeed;
		Unloaded += (_, _) => applier.SpeedChanged -= onSpeed;

		AutoUpdateBox.IsChecked = ViewModel.AutoCheckUpdates;
		PreReleaseBox.IsChecked = ViewModel.IncludePreReleases;
		var updates = App.Services.GetRequiredService<UpdateService>();
		EventHandler onUpdate = (_, _) => DispatcherQueue.TryEnqueue(RefreshUpdateStatus);
		Loaded += (_, _) => {
			updates.Changed += onUpdate;
			RefreshUpdateStatus();
		};
		Unloaded += (_, _) => updates.Changed -= onUpdate;
	}

	// ---------------------------------------------------------------- updates

	private static UpdateService Updates => App.Services.GetRequiredService<UpdateService>();

	private void AutoUpdate_Click(object sender, RoutedEventArgs e) => ViewModel.AutoCheckUpdates = AutoUpdateBox.IsChecked == true;

	private void PreRelease_Click(object sender, RoutedEventArgs e) => ViewModel.IncludePreReleases = PreReleaseBox.IsChecked == true;

	private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await Updates.CheckAsync(manual: true);

	private async void RestartUpdate_Click(object sender, RoutedEventArgs e) => await Updates.RestartAsync();

	private void RefreshUpdateStatus() {
		var updates = Updates;
		var installed = updates.State != UpdateState.NotInstalled && updates.IsInstalled;
		CheckUpdatesButton.IsEnabled = installed && updates.State is not (UpdateState.Checking or UpdateState.Downloading);
		RestartUpdateButton.Visibility = updates.State == UpdateState.Ready ? Visibility.Visible : Visibility.Collapsed;
		UpdateStatus.Text = !installed
			? Loc.S("Update_NotInstalled", "This copy was not installed with Setup.exe (portable copy or a build from source), so it cannot update itself. Download a newer version from the Releases page.")
			: updates.State switch {
				UpdateState.Checking => Loc.S("Update_Checking", "Checking for updates…"),
				UpdateState.UpToDate => Loc.F("Update_UpToDate", "Aria2NET {0} is up to date.", AboutViewModel.AppVersion),
				UpdateState.Downloading => Loc.F("Update_Downloading", "Downloading version {0}…", updates.NewVersion ?? ""),
				UpdateState.Ready => Loc.F("Update_Ready", "Version {0} is ready. Restart to install it.", updates.NewVersion ?? ""),
				UpdateState.Failed => Loc.F("Update_Failed", "Could not check for updates: {0}", updates.Error ?? ""),
				_ => "",
			};
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
			AllocationBox.SelectedIndex = (int) s.FileAllocation;
			DownLimitBox.Value = s.DownloadLimitKiBps;
			UpLimitBox.Value = s.UploadLimitKiBps;
			AltDownLimitBox.Value = s.AltDownloadLimitKiBps;
			AltUpLimitBox.Value = s.AltUploadLimitKiBps;
			AltSpeedSwitch.IsOn = s.AltSpeedActive;
			ProxyModeBox.SelectedIndex = (int) s.Proxy;
			ProxyFields.Visibility = s.Proxy == ProxyMode.Manual ? Visibility.Visible : Visibility.Collapsed;
			ProxyAddressBox.Text = s.ProxyAddress ?? "";
			ProxyUserBox.Text = s.ProxyUser ?? "";
			ProxyPasswordBox.Password = SecretProtector.Unprotect(s.ProxyPasswordProtected);
			CertSwitch.IsOn = s.CheckCertificates;
			Ipv6Switch.IsOn = s.UseIPv6;
			ProxyStatus.Text = ProxyStatusText(s);
			LogLevelBox.SelectedIndex = (int) s.LogLevel;
			RawOptionsBox.Text = ViewModel.RawOptionsText;
			ThemeBox.SelectedIndex = (int) s.Theme;
			ConflictBox.SelectedIndex = (int) s.Conflict;
			TrackerSourceBox.SelectedIndex = (int) s.Trackers;
			TrackerUrlBox.Visibility = s.Trackers == TrackerSource.Custom ? Visibility.Visible : Visibility.Collapsed;
			TrackerUrlBox.Text = s.TrackerUrl ?? "";
			PortStartBox.Value = s.ListenPortStart;
			PortEndBox.Value = s.ListenPortEnd;
			DhtPortBox.Value = s.DhtPort;
			DhtBox.IsChecked = s.EnableDht;
			PexBox.IsChecked = s.EnablePex;
			LpdBox.IsChecked = s.EnableLpd;
			EncryptionBox.SelectedIndex = (int) s.Encryption;
			SeedRatioBox.Value = s.SeedRatio;
			SeedTimeBox.Value = s.SeedTimeMinutes;
			TorrentStatus.Text = EngineOptions.ValidateTorrent(s) ?? "";
			RestartBar.IsOpen = ViewModel.RestartRequired;
		} finally {
			_loading = false;
		}
		_ = RefreshFolderStatusAsync();
	}

	private static int Int(NumberBox box) => (int) Math.Round(box.Value);

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
			ViewModel.Update(s => s.FileAllocation = (FileAllocationMode) AllocationBox.SelectedIndex);
		}
	}

	private void AltSpeed_Toggled(object sender, RoutedEventArgs e) {
		if (!_loading) {
			_ = ViewModel.SetAltSpeedAsync(AltSpeedSwitch.IsOn);
		}
	}

	private static string ProxyStatusText(AppSettings s) => s.Proxy switch {
		ProxyMode.Manual => EngineOptions.ValidateProxy(s) ?? "",
		ProxyMode.System => EngineOptions.NetworkOptions(s)["all-proxy"] is { Length: > 0 } p
			? Loc.F("Settings_ProxyUsing", "Using {0} from Windows settings (read when settings are applied or the engine starts).", p)
			: Loc.S("Settings_ProxyNoneSet", "Windows has no proxy set, so downloads connect directly."),
		_ => "",
	};

	private void ProxyMode_Changed(object sender, SelectionChangedEventArgs e) {
		if (_loading || ProxyModeBox.SelectedIndex < 0) {
			return;
		}
		var mode = (ProxyMode) ProxyModeBox.SelectedIndex;
		ProxyFields.Visibility = mode == ProxyMode.Manual ? Visibility.Visible : Visibility.Collapsed;
		ViewModel.Update(s => s.Proxy = mode);
		ProxyStatus.Text = ProxyStatusText(ViewModel.Current);
	}

	private void ProxyField_LostFocus(object sender, RoutedEventArgs e) {
		if (_loading) {
			return;
		}
		ViewModel.Update(s => {
			s.ProxyAddress = ProxyAddressBox.Text.Trim();
			s.ProxyUser = ProxyUserBox.Text.Trim();
		});
		ProxyStatus.Text = ProxyStatusText(ViewModel.Current);
	}

	private void ProxyPassword_LostFocus(object sender, RoutedEventArgs e) {
		if (!_loading) {
			ViewModel.SetProxyPassword(ProxyPasswordBox.Password);
		}
	}

	private void Network_Toggled(object sender, RoutedEventArgs e) {
		if (!_loading) {
			ViewModel.Update(s => {
				s.CheckCertificates = CertSwitch.IsOn;
				s.UseIPv6 = Ipv6Switch.IsOn;
			});
			RestartBar.IsOpen = ViewModel.RestartRequired; // aria2 reads both only at launch
		}
	}

	/// <summary>Saves a torrent setting unless it would make the settings invalid; the reason is shown under the tracker choice.</summary>
	private void TorrentChange(Action<AppSettings> apply) {
		if (_loading) {
			return;
		}
		var candidate = ViewModel.Current;
		apply(candidate);
		if (EngineOptions.ValidateTorrent(candidate) is { } problem) {
			TorrentStatus.Text = problem;
			return;
		}
		TorrentStatus.Text = "";
		ViewModel.Update(apply);
		RestartBar.IsOpen = ViewModel.RestartRequired; // ports and DHT are read only at launch
	}

	private void TorrentNumber_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args) {
		if (_loading) {
			return;
		}
		if (double.IsNaN(sender.Value)) {
			sender.Value = double.IsNaN(args.OldValue) ? sender.Minimum : args.OldValue;
			return;
		}
		TorrentChange(s => {
			s.ListenPortStart = Int(PortStartBox);
			s.ListenPortEnd = Int(PortEndBox);
			s.DhtPort = Int(DhtPortBox);
			s.SeedRatio = Math.Round(SeedRatioBox.Value, 2);
			s.SeedTimeMinutes = Int(SeedTimeBox);
		});
	}

	private void TorrentToggle_Click(object sender, RoutedEventArgs e) => TorrentChange(s => {
		s.EnableDht = DhtBox.IsChecked == true;
		s.EnablePex = PexBox.IsChecked == true;
		s.EnableLpd = LpdBox.IsChecked == true;
	});

	private void Encryption_Changed(object sender, SelectionChangedEventArgs e) {
		if (EncryptionBox.SelectedIndex >= 0) {
			TorrentChange(s => s.Encryption = (TorrentEncryption) EncryptionBox.SelectedIndex);
		}
	}

	private async void TrackerSource_Changed(object sender, SelectionChangedEventArgs e) {
		if (_loading || TrackerSourceBox.SelectedIndex < 0) {
			return;
		}
		var source = (TrackerSource) TrackerSourceBox.SelectedIndex;
		TrackerUrlBox.Visibility = source == TrackerSource.Custom ? Visibility.Visible : Visibility.Collapsed;
		TorrentChange(s => s.Trackers = source);
		if (source != TrackerSource.Custom || EngineOptions.ValidateTorrent(ViewModel.Current) is null) {
			await RefreshTrackersAsync(); // a different source means a different list
		}
	}

	private async void TrackerUrl_LostFocus(object sender, RoutedEventArgs e) {
		if (_loading || TrackerUrlBox.Text.Trim() == (ViewModel.Current.TrackerUrl ?? "")) {
			return;
		}
		TorrentChange(s => s.TrackerUrl = TrackerUrlBox.Text.Trim());
		if (EngineOptions.ValidateTorrent(ViewModel.Current) is null) {
			await RefreshTrackersAsync();
		}
	}

	// ---------------------------------------------------------------- search and jump

	private sealed record SettingsSection(string Title, StackPanel Panel);

	private readonly List<SettingsSection> _sections = [];

	/// <summary>Groups the page's controls into one panel per section (each starts at a header marked Tag="section") so a section can be hidden or scrolled to.</summary>
	private void BuildSections() {
		var children = PageRoot.Children.ToList();
		var first = children.FindIndex(IsSectionHeader);
		if (first < 0) {
			return;
		}
		StackPanel? current = null;
		foreach (var child in children.Skip(first)) {
			PageRoot.Children.Remove(child);
			if (IsSectionHeader(child)) {
				current = new StackPanel { Spacing = 12 };
				_sections.Add(new SettingsSection(((TextBlock) child).Text, current));
			}
			current!.Children.Add(child);
		}
		foreach (var section in _sections) {
			PageRoot.Children.Add(section.Panel);
			JumpBox.Items.Add(section.Title);
		}
	}

	private static bool IsSectionHeader(UIElement element) => element is TextBlock { Tag: "section" };

	private static void CollectText(UIElement element, System.Text.StringBuilder text) {
		switch (element) {
			case Panel panel:
				foreach (var child in panel.Children) {
					CollectText(child, text);
				}
				break;
			case TextBlock block:
				text.Append(block.Text).Append(' ');
				break;
			case ComboBox combo:
				text.Append(combo.Header).Append(' ');
				foreach (var item in combo.Items) {
					text.Append(item).Append(' ');
				}
				break;
			case TextBox box:
				text.Append(box.Header).Append(' ');
				break;
			case PasswordBox password:
				text.Append(password.Header).Append(' ');
				break;
			case NumberBox number:
				text.Append(number.Header).Append(' ');
				break;
			case ToggleSwitch toggle:
				text.Append(toggle.Header).Append(' ');
				break;
			case ContentControl { Content: string content }:
				text.Append(content).Append(' ');
				break;
		}
	}

	private void Search_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) {
		var words = sender.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		var anyVisible = false;
		foreach (var section in _sections) {
			var visible = words.Length == 0;
			if (!visible) {
				var text = new System.Text.StringBuilder(section.Title).Append(' ');
				CollectText(section.Panel, text);
				var haystack = text.ToString();
				visible = words.All(w => haystack.Contains(w, StringComparison.CurrentCultureIgnoreCase));
			}
			section.Panel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
			anyVisible |= visible;
		}
		SearchEmpty.Visibility = anyVisible ? Visibility.Collapsed : Visibility.Visible;
	}

	private void Jump_Changed(object sender, SelectionChangedEventArgs e) {
		var index = JumpBox.SelectedIndex;
		if (index < 0 || index >= _sections.Count) {
			return;
		}
		SearchBox.Text = ""; // a hidden section cannot be scrolled to
		_sections[index].Panel.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0 });
		JumpBox.SelectedIndex = -1;
	}

	// ---------------------------------------------------------------- advanced

	private void LogLevel_Changed(object sender, SelectionChangedEventArgs e) {
		if (!_loading && LogLevelBox.SelectedIndex >= 0) {
			var level = (AppLogLevel) LogLevelBox.SelectedIndex;
			ViewModel.Update(s => s.LogLevel = level);
		}
	}

	private void RawOptions_LostFocus(object sender, RoutedEventArgs e) {
		if (_loading || RawOptionsBox.Text == ViewModel.RawOptionsText) {
			return;
		}
		var error = ViewModel.SetRawOptions(RawOptionsBox.Text);
		RawOptionsStatus.Text = error ?? "";
		if (error is null) {
			RestartBar.IsOpen = ViewModel.RestartRequired;
		}
	}

	private async void ExportSettings_Click(object sender, RoutedEventArgs e) {
		try {
			var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = "aria2net-settings", SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary };
			picker.FileTypeChoices.Add("Aria2NET settings", [".json"]);
			WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.Window));
			if (await picker.PickSaveFileAsync() is not { } file) {
				return;
			}
			await Windows.Storage.FileIO.WriteTextAsync(file, ViewModel.ExportJson());
			TransferStatus.Text = Loc.F("Settings_Exported", "Exported to {0}.", file.Path);
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) {
			TransferStatus.Text = Loc.F("Settings_ExportFailed", "Could not export: {0}", ex.Message);
		}
	}

	private async void ImportSettings_Click(object sender, RoutedEventArgs e) {
		try {
			var picker = new Windows.Storage.Pickers.FileOpenPicker();
			picker.FileTypeFilter.Add(".json");
			WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.Window));
			if (await picker.PickSingleFileAsync() is not { } file) {
				return;
			}
			if ((await file.GetBasicPropertiesAsync()).Size > SettingsTransfer.MaxImportBytes) {
				TransferStatus.Text = Loc.S("Settings_TooLarge", "This file is too large to be a settings file.");
				return;
			}
			var error = ViewModel.ImportJson(await Windows.Storage.FileIO.ReadTextAsync(file));
			if (error is not null) {
				TransferStatus.Text = Loc.F("Settings_NotImported", "Nothing was imported. {0}", error);
				return;
			}
			Frame.Navigate(typeof(SettingsPage), null, new Microsoft.UI.Xaml.Media.Animation.SuppressNavigationTransitionInfo()); // reload every control
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) {
			TransferStatus.Text = Loc.F("Settings_ImportFailed", "Could not import: {0}", ex.Message);
		}
	}

	private async void ResetAll_Click(object sender, RoutedEventArgs e) {
		if (!await App.Window.ConfirmAsync(Loc.S("Settings_ResetTitle", "Reset all settings?"), Loc.S("Settings_ResetBody", "Every setting on this page goes back to its default, including the proxy and any hand-typed aria2 options. Downloads and history are not touched."), Loc.S("Settings_ResetConfirm", "Reset"))) {
			return;
		}
		if (ViewModel.ResetAll() is { } error) {
			TransferStatus.Text = error;
			return;
		}
		Frame.Navigate(typeof(SettingsPage), null, new Microsoft.UI.Xaml.Media.Animation.SuppressNavigationTransitionInfo());
	}

	private void Theme_Changed(object sender, SelectionChangedEventArgs e) {
		if (_loading || ThemeBox.SelectedIndex < 0) {
			return;
		}
		var theme = (AppTheme) ThemeBox.SelectedIndex;
		ViewModel.Update(s => s.Theme = theme);
		App.ApplyTheme(theme);
	}

	private void Conflict_Changed(object sender, SelectionChangedEventArgs e) {
		if (!_loading && ConflictBox.SelectedIndex >= 0) {
			var policy = (ConflictPolicy) ConflictBox.SelectedIndex;
			ViewModel.Update(s => s.Conflict = policy);
		}
	}

	private void ResetTorrents_Click(object sender, RoutedEventArgs e) {
		ViewModel.ResetTorrents();
		LoadEngineSettings();
		RestartBar.IsOpen = ViewModel.RestartRequired;
	}

	private void ResetNetwork_Click(object sender, RoutedEventArgs e) {
		ViewModel.ResetNetwork();
		LoadEngineSettings();
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
		if (await App.Window.ConfirmAsync(Loc.S("History_ClearTitle", "Clear all history?"), Loc.S("History_ClearBody", "Every finished, failed and removed entry is forgotten. Downloads still in progress stay. Downloaded files are not deleted."), Loc.S("History_ClearConfirm", "Clear history"))) {
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
		if (FinishBox.SelectedIndex >= 0 && FinishBox.SelectedIndex != (int) FinishService.Action) {
			FinishService.Action = (FinishAction) FinishBox.SelectedIndex;
		}
	}

	private void Hotkeys_Click(object sender, RoutedEventArgs e) {
		ViewModel.HotkeysEnabled = HotkeysBox.IsChecked == true;
		var free = App.Services.GetRequiredService<BackgroundCoordinator>().ApplyHotkeys();
		HotkeysStatus.Text = free ? "" : Loc.S("Settings_HotkeysTaken", "Another program already uses one of these shortcuts, so it is not active.");
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
				? Loc.S("Settings_AssocDefault", "Aria2NET is the default app for .torrent files and magnet links.")
				: Loc.S("Settings_AssocOffered", "Aria2NET is offered to Windows. Windows lets only you choose the default app: pick Aria2NET for .torrent files and for the MAGNET link type.");
	}

	private async void Association_Click(object sender, RoutedEventArgs e) {
		if (AssociationBox.IsChecked == true) {
			if (!Association.Enable()) {
				RefreshAssociation();
				await TorrentAssociation.OpenDefaultAppsAsync();
			}
		} else {
			Association.Disable();
		}
		RefreshAssociation();
	}

	private async void DefaultApps_Click(object sender, RoutedEventArgs e) => await TorrentAssociation.OpenDefaultAppsAsync();

	private static string TrackerSummary() {
		var paths = App.Services.GetRequiredService<AppPaths>();
		var count = TrackerListService.ReadCache(paths).Count;
		return count == 0 ? Loc.S("Settings_NoTrackerList", "No list downloaded yet.") : Loc.F("Settings_TrackersCached", "{0} trackers cached; refreshed automatically every week.", count);
	}

	private async void UpdateTrackers_Click(object sender, RoutedEventArgs e) => await RefreshTrackersAsync();

	private async Task RefreshTrackersAsync() {
		var url = TrackerListService.SourceUrl(ViewModel.Current);
		if (url is null) {
			TrackerStatus.Text = ViewModel.Current.Trackers == TrackerSource.Off ? Loc.S("Settings_TrackersSwitchedOff", "Extra trackers are switched off.") : Loc.S("Settings_TrackersNeedUrl", "Enter a valid https:// address first.");
			return;
		}
		TrackerStatus.Text = Loc.S("Settings_Downloading", "Downloading…");
		var list = await App.Services.GetRequiredService<TrackerListService>().RefreshAsync(url);
		if (list is not null && App.Services.GetRequiredService<IAria2Engine>().Rpc is { } rpc) {
			try {
				await rpc.ChangeGlobalOptionAsync(new Dictionary<string, string> { ["bt-tracker"] = Aria2Config.TrackerOptionValue(list) });
			} catch (Exception ex) when (ex is Aria2RpcException or HttpRequestException) {
				// The new list is cached and used at the next engine start.
			}
		}
		TrackerStatus.Text = list is null ? Loc.S("Settings_TrackerDownloadFailed", "Could not download the list; keeping the old one.") : TrackerSummary();
	}
}
