using System.Text.Json.Serialization;

namespace Aria2NET.Core.Settings;

public sealed class AppSettings {
	public int SchemaVersion { get; set; } = 1;
	/// <summary>Folder used when nothing else was chosen; null means the user's Downloads folder.</summary>
	public string? DefaultDownloadFolder { get; set; }
	/// <summary>Folder picked in the last Add dialog.</summary>
	public string? LastUsedFolder { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
public sealed partial class AppSettingsJsonContext : JsonSerializerContext;
