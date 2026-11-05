namespace Aria2NET.Core;

/// <summary>Single place that knows where app data, logs and the bundled tools live.</summary>
public sealed class AppPaths(string dataRoot, string toolsDir) {
	/// <summary>Set to a folder to keep all app data there instead of %LocalAppData%\Aria2NET (portable use, tests, trying a build without touching real data).</summary>
	public const string DataDirVariable = "ARIA2NET_DATA_DIR";

	public static AppPaths CreateDefault() => new(
		Environment.GetEnvironmentVariable(DataDirVariable) is { Length: > 0 } custom
			? Path.GetFullPath(custom)
			: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aria2NET"),
		Path.Combine(AppContext.BaseDirectory, "tools", "aria2"));

	public string DataRoot { get; } = dataRoot;
	public string ToolsDir { get; } = toolsDir;

	public string LogsDir => Path.Combine(DataRoot, "logs");
	public string EngineDir => Path.Combine(DataRoot, "engine");
	public string BackupsDir => Path.Combine(DataRoot, "backups");
	public string SettingsFile => Path.Combine(DataRoot, "settings.json");
	public string DatabaseFile => Path.Combine(DataRoot, "history.db");
	public string LockFile => Path.Combine(DataRoot, "aria2net.lock");
	public string Aria2Exe => Path.Combine(ToolsDir, "aria2c.exe");
	public string Aria2ConfFile => Path.Combine(EngineDir, "aria2.conf");
	public string Aria2SessionFile => Path.Combine(EngineDir, "aria2.session");
	public string Aria2LogFile => Path.Combine(LogsDir, "aria2.log");

	public void EnsureDirectories() {
		Directory.CreateDirectory(DataRoot);
		Directory.CreateDirectory(LogsDir);
		Directory.CreateDirectory(EngineDir);
		Directory.CreateDirectory(BackupsDir);
	}
}
