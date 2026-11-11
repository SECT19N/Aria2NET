using Aria2NET.Core.Settings;
using Aria2NET.Core.Shell;
using Microsoft.Extensions.Logging;

namespace Aria2NET.Services;

/// <summary>Opening .torrent/.metalink files and magnet links with Aria2NET: registering, unregistering, and the one-time offer.</summary>
public sealed class TorrentAssociation(SettingsStore<AppSettings> settings, ILogger<TorrentAssociation> logger) {
	private readonly ShellRegistration? _registration = Environment.ProcessPath is { Length: > 0 } exe ? new ShellRegistration(exe) : null;

	public bool IsAvailable => _registration is not null;

	public ShellRegistrationState State => _registration?.GetState() ?? new ShellRegistrationState(false, false, false);

	/// <summary>True until the user has been asked once (and also not when they already set it up themselves).</summary>
	public bool ShouldOffer => IsAvailable && !settings.Load().TorrentAssociationPrompted && !State.Registered;

	public void MarkOffered() {
		try {
			var s = settings.Load();
			s.TorrentAssociationPrompted = true;
			settings.Save(s);
		} catch (IOException ex) {
			logger.LogWarning(ex, "Could not remember that the association was offered");
		}
	}

	/// <summary>Registers the app. Windows still asks the user to pick it as the default, so this returns whether that has happened already.</summary>
	public bool Enable() {
		_registration?.Register();
		var state = State;
		logger.LogInformation("Registered for torrent files and magnet links (default for .torrent: {Torrent}, magnet: {Magnet})", state.DefaultForTorrent, state.DefaultForMagnet);
		return state.DefaultForTorrent && state.DefaultForMagnet;
	}

	public void Disable() {
		_registration?.Unregister();
		logger.LogInformation("Unregistered from torrent files and magnet links");
	}

	/// <summary>Opens Windows Settings at the Default apps page, where the user picks Aria2NET.</summary>
	public static async Task OpenDefaultAppsAsync() {
		try {
			await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:defaultapps"));
		} catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) {
			// Settings could not be opened; the user can still get there by hand.
		}
	}
}
