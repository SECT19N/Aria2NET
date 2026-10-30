namespace Aria2NET.Core;

/// <summary>Single place that knows where app data, logs and the bundled tools live.</summary>
public sealed class AppPaths {
	public AppPaths(string dataRoot, string toolsDir) {
		DataRoot = dataRoot;
		ToolsDir = toolsDir;
	}

	public static AppPaths CreateDefault() => new(
		Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aria2NET"),
		Path.Combine(AppContext.BaseDirectory, "tools", "aria2"));

	public string DataRoot { get; }
	public string ToolsDir { get; }

	public string LogsDir => Path.Combine(DataRoot, "logs");
	public string EngineDir => Path.Combine(DataRoot, "engine");
	public string SettingsFile => Path.Combine(DataRoot, "settings.json");
	public string Aria2Exe => Path.Combine(ToolsDir, "aria2c.exe");
	public string Aria2ConfFile => Path.Combine(EngineDir, "aria2.conf");
	public string Aria2SessionFile => Path.Combine(EngineDir, "aria2.session");
	public string Aria2LogFile => Path.Combine(LogsDir, "aria2.log");

	public void EnsureDirectories() {
		Directory.CreateDirectory(DataRoot);
		Directory.CreateDirectory(LogsDir);
		Directory.CreateDirectory(EngineDir);
	}
}
