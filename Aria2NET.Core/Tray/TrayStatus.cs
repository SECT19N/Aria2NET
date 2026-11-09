using Aria2NET.Core.Aria2;
using Aria2NET.Core.Downloads;

namespace Aria2NET.Core.Tray;

public enum TrayState {
	Idle,
	Downloading,
	/// <summary>Nothing runs and at least one download is paused.</summary>
	Paused,
	/// <summary>The engine crashed or could not start.</summary>
	Error,
}

/// <summary>What the notification-area icon shows: a state (drawn as a coloured dot on the app icon) and the tooltip text.</summary>
public sealed record TrayStatus(TrayState State, string Tooltip) {
	private const string Name = "Aria2NET";

	public static TrayStatus From(PollSnapshot? snapshot, EngineState engine) {
		if (engine is EngineState.Failed or EngineState.Crashed) {
			return new TrayStatus(TrayState.Error, $"{Name}\nThe download engine is not running");
		}
		if (engine != EngineState.Running || snapshot is null) {
			return new TrayStatus(TrayState.Idle, Name);
		}

		var active = 0;
		var seeding = 0;
		var paused = 0;
		foreach (var item in snapshot.Items) {
			switch (item.Status) {
				case DownloadStatus.Active: active++; break;
				case DownloadStatus.Seeding: seeding++; break;
				case DownloadStatus.Paused: paused++; break;
			}
		}

		if (active > 0) {
			return new TrayStatus(TrayState.Downloading, $"{Name}\n↓ {ByteFormat.Speed(snapshot.Stats.DownloadSpeed)} · {active} active");
		}
		if (seeding > 0) {
			return new TrayStatus(TrayState.Idle, $"{Name}\nSeeding {seeding} · ↑ {ByteFormat.Speed(snapshot.Stats.UploadSpeed)}");
		}
		if (paused > 0) {
			return new TrayStatus(TrayState.Paused, $"{Name}\n{paused} paused");
		}
		return new TrayStatus(TrayState.Idle, $"{Name}\nIdle");
	}
}
