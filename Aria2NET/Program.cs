using Aria2NET.Core.Shell;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;

namespace Aria2NET;

public static class Program {
	[STAThread]
	private static int Main(string[] args) {
		WinRT.ComWrappersSupport.InitializeComWrappers();

		// Second launch: hand over to the running instance and exit.
		var instance = AppInstance.FindOrRegisterForKey("Aria2NET.Main");
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

	/// <summary>Links and files that a second launch carried to this instance.</summary>
	private static IReadOnlyList<string> ItemsOf(AppActivationArguments e) => e.Kind switch {
		ExtendedActivationKind.Launch when e.Data is ILaunchActivatedEventArgs launch => LaunchArguments.Extract(LaunchArguments.Split(launch.Arguments)),
		ExtendedActivationKind.File when e.Data is IFileActivatedEventArgs file => LaunchArguments.Extract(file.Files.Select(f => f.Path)),
		ExtendedActivationKind.Protocol when e.Data is IProtocolActivatedEventArgs protocol => LaunchArguments.Extract([protocol.Uri.OriginalString]),
		_ => [],
	};
}
