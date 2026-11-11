using System.Reflection;
using Aria2NET.Core.Aria2;

namespace Aria2NET.ViewModels;

public sealed class AboutViewModel {
	public AboutViewModel(IAria2Engine engine) {
		Aria2Version = engine.Version?.Version ?? Loc.S("About_NotRunning", "not running");
	}

	public static string AppVersion { get; } =
		(Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.1.0")
			.Split('+')[0];

	public string Aria2Version { get; }

	public string AppVersionText => Loc.F("About_Version", "Version {0}", AppVersion);

	public string Aria2VersionText => Loc.F("About_Aria2Version", "aria2 engine: {0}", Aria2Version);

	/// <summary>Text of a file shipped next to the exe (the license, the notices), or a short explanation when it is missing.</summary>
	public static string ReadShippedText(string fileName) {
		try {
			var path = Path.Combine(AppContext.BaseDirectory, fileName);
			return File.Exists(path) ? File.ReadAllText(path) : Loc.F("About_FileMissing", "{0} was not found next to the app. It is also in the project repository.", fileName);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			return Loc.F("About_FileUnreadable", "{0} could not be read: {1}", fileName, ex.Message);
		}
	}

	public string License => Loc.S("About_License", "Aria2NET is free software under the GNU General Public License v3. It bundles aria2 (GPL-2.0-or-later with OpenSSL exception).");
}
