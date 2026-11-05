namespace Aria2NET.Core.Aria2;

public enum EngineState {
	Stopped,
	Starting,
	Running,
	Stopping,
	Restarting,
	/// <summary>aria2c exited unexpectedly while running.</summary>
	Crashed,
	/// <summary>aria2c could not be started (missing binary, RPC never answered, ...).</summary>
	Failed,
}

public enum EngineNoticeKind {
	/// <summary>aria2's session file was damaged and had to be replaced.</summary>
	SessionRepaired,
}

/// <summary>Something that happened during a start that the user should hear about.</summary>
public sealed record EngineNotice(EngineNoticeKind Kind, string Message);

public interface IAria2Engine : IAsyncDisposable {
	EngineState State { get; }
	/// <summary>Human-readable reason for <see cref="EngineState.Failed"/> / <see cref="EngineState.Crashed"/>.</summary>
	string? LastError { get; }
	/// <summary>aria2 version once running, otherwise null.</summary>
	Aria2Version? Version { get; }
	/// <summary>RPC client for the running engine, otherwise null.</summary>
	Aria2RpcClient? Rpc { get; }

	event EventHandler? StateChanged;
	/// <summary>Raised on whichever thread starts the engine.</summary>
	event EventHandler<EngineNotice>? NoticeRaised;

	Task StartAsync(CancellationToken ct = default);
	Task StopAsync(CancellationToken ct = default);
	Task RestartAsync(CancellationToken ct = default);
}
