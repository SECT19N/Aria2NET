using System.ComponentModel;
using System.Diagnostics;

namespace Aria2NET.Core.Shell;

/// <summary>Opens a downloaded file, or its folder in Explorer. Shared by the lists, the tray menu and toast clicks.</summary>
public static class ShellLauncher {
	/// <summary>Opens the file with its default app. <paramref name="error"/> is set when Windows refuses.</summary>
	public static bool TryOpenFile(string path, out string error) => TryStart(new ProcessStartInfo(path) { UseShellExecute = true }, out error);

	/// <summary>Opens <paramref name="dir"/> in Explorer, selecting <paramref name="file"/> when that file exists.</summary>
	public static bool TryOpenFolder(string dir, string? file, out string error) =>
		TryStart(new ProcessStartInfo("explorer.exe", FolderArguments(dir, file)) { UseShellExecute = true }, out error);

	/// <summary>Explorer arguments: select the file when it is there, otherwise just show the folder.</summary>
	public static string FolderArguments(string dir, string? file) => file is not null && File.Exists(file) ? $"/select,\"{file}\"" : $"\"{dir}\"";

	private static bool TryStart(ProcessStartInfo info, out string error) {
		try {
			Process.Start(info);
			error = "";
			return true;
		}
		catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) {
			error = ex.Message;
			return false;
		}
	}
}
