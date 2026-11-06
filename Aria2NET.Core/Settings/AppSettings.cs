using System.Text.Json.Serialization;

namespace Aria2NET.Core.Settings;

public sealed class AppSettings {
	public int SchemaVersion { get; set; } = 1;
	/// <summary>Folder used when nothing else was chosen; null means the user's Downloads folder.</summary>
	public string? DefaultDownloadFolder { get; set; }
	/// <summary>Folder picked in the last Add dialog.</summary>
	public string? LastUsedFolder { get; set; }
	/// <summary>Ask before "Remove and delete file". Cleared by the "Don't ask again" box.</summary>
	public bool ConfirmDeleteFiles { get; set; } = true;
	public string StatusFilter { get; set; } = "All";
	public string SortColumn { get; set; } = "None";
	public bool SortDescending { get; set; }
	/// <summary>How long finished downloads stay in History: 0 keeps them forever, otherwise a number of days (30, 90, 365).</summary>
	public int HistoryRetentionDays { get; set; }
	/// <summary>The one-time offer to open .torrent files and magnet links with Aria2NET has been shown.</summary>
	public bool TorrentAssociationPrompted { get; set; }
	public WindowPlacement? Window { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
public sealed partial class AppSettingsJsonContext : JsonSerializerContext;
