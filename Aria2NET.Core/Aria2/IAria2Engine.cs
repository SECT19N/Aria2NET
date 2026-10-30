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

public interface IAria2Engine : IAsyncDisposable {
	EngineState State { get; }
	/// <summary>Human-readable reason for <see cref="EngineState.Failed"/> / <see cref="EngineState.Crashed"/>.</summary>
	string? LastError { get; }
	/// <summary>aria2 version once running, otherwise null.</summary>
	Aria2Version? Version { get; }
	/// <summary>RPC client for the running engine, otherwise null.</summary>
	Aria2RpcClient? Rpc { get; }

	event EventHandler? StateChanged;

	Task StartAsync(CancellationToken ct = default);
	Task StopAsync(CancellationToken ct = default);
	Task RestartAsync(CancellationToken ct = default);
}
