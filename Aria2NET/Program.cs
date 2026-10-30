using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

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

		instance.Activated += (_, _) => App.ActivateMainWindow();

		Application.Start(p => {
			SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
			_ = new App();
		});
		return 0;
	}
}
