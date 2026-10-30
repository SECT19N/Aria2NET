using System.Reflection;
using Aria2NET.Core.Aria2;

namespace Aria2NET.ViewModels;

public sealed class AboutViewModel {
	public AboutViewModel(IAria2Engine engine) {
		Aria2Version = engine.Version?.Version ?? "not running";
	}

	public static string AppVersion { get; } =
		(Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.1.0")
			.Split('+')[0];

	public string Aria2Version { get; }

	public string AppVersionText => $"Version {AppVersion}";

	public string Aria2VersionText => $"aria2 engine: {Aria2Version}";

	public string License => "Aria2NET is free software under the GNU General Public License v3. It bundles aria2 (GPL-2.0-or-later with OpenSSL exception).";
}
