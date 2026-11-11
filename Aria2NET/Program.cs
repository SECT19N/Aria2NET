using Aria2NET.Core.Shell;
using Aria2NET.Services;
using Aria2NET.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Velopack;
using Windows.ApplicationModel.Activation;

namespace Aria2NET;

public static class Program {
	[STAThread]
	private static int Main(string[] args) {
		// First, before anything else: when the installer or updater starts the exe with a hook argument (install, update, uninstall) this
		// runs the hook and exits, so it never reaches the single-instance check below and cannot be redirected to a running copy.
		VelopackApp.Build()
			.OnBeforeUninstallFastCallback(_ => RemoveUserRegistrations())
			.Run();

		WinRT.ComWrappersSupport.InitializeComWrappers();

		// Second launch: hand over to the running instance and exit.
		var instance = AppInstance.FindOrRegisterForKey("Aria2NET.Main");
		// An older copy that is still running (left in the tray, or started at sign-in from an old folder) would take this launch over.
		// A newer copy asks it to leave first, then becomes the main instance itself.
		if (!instance.IsCurrent && InstanceRules.ParseVersion(AboutViewModel.AppVersion) is { } mine && InstanceTakeover.ReplaceOlderCopies(mine)) {
			instance = AppInstance.FindOrRegisterForKey("Aria2NET.Main");
		}
		if (!instance.IsCurrent) {
			var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
			Task.Run(() => instance.RedirectActivationToAsync(activation).AsTask()).GetAwaiter().GetResult();
			return 0;
		}

		// A .torrent file or magnet link that started the app (double-click, browser link).
		App.StartupItems = LaunchArguments.Extract(args);
		App.StartedAtSignIn = LaunchArguments.IsAutostart(args);
		instance.Activated += (_, e) => {
			App.ActivateMainWindow();
			App.HandleExternalLaunch(ItemsOf(e));
		};

		Application.Start(p => {
			SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
			_ = new App();
		});
		return 0;
	}

	/// <summary>Uninstall: take out the sign-in entry and the torrent/magnet registration so nothing keeps pointing at a deleted exe. User data stays.</summary>
	private static void RemoveUserRegistrations() {
		try {
			new StartupRegistration(Environment.ProcessPath ?? "").Disable();
			new ShellRegistration(Environment.ProcessPath ?? "").Unregister();
		} catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) {
			// Uninstalling must not fail over a registry key.
		}
	}

	/// <summary>Links and files that a second launch carried to this instance.</summary>
	private static IReadOnlyList<string> ItemsOf(AppActivationArguments e) => e.Kind switch {
		ExtendedActivationKind.Launch when e.Data is ILaunchActivatedEventArgs launch => LaunchArguments.Extract(LaunchArguments.Split(launch.Arguments)),
		ExtendedActivationKind.File when e.Data is IFileActivatedEventArgs file => LaunchArguments.Extract(file.Files.Select(f => f.Path)),
		ExtendedActivationKind.Protocol when e.Data is IProtocolActivatedEventArgs protocol => LaunchArguments.Extract([protocol.Uri.OriginalString]),
		_ => [],
	};
}
