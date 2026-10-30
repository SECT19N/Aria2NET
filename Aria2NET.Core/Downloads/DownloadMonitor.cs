using Aria2NET.Core.Aria2;
using Microsoft.Extensions.Logging;

namespace Aria2NET.Core.Downloads;

/// <summary>Polls aria2 once per interval and publishes the newest snapshot. Reads the engine's RPC client on every tick, so it survives restarts.</summary>
public sealed class DownloadMonitor(IAria2Engine engine, ILogger<DownloadMonitor> logger, TimeSpan? interval = null) {
	private readonly TimeSpan _interval = interval ?? TimeSpan.FromSeconds(1);
	private CancellationTokenSource? _cts;
	private Task? _loop;
	private bool _lastTickFailed;

	/// <summary>Raised on a background thread.</summary>
	public event EventHandler<PollSnapshot>? SnapshotReady;

	public void Start() {
		if (_cts is not null) {
			return;
		}
		_cts = new CancellationTokenSource();
		_loop = RunAsync(_cts.Token);
	}

	public async Task StopAsync() {
		if (_cts is null) {
			return;
		}
		await _cts.CancelAsync();
		try {
			await _loop!;
		}
		catch (OperationCanceledException) {
			// Expected.
		}
		_cts.Dispose();
		_cts = null;
		_loop = null;
	}

	private async Task RunAsync(CancellationToken ct) {
		using var timer = new PeriodicTimer(_interval);
		do {
			await PollOnceAsync(ct);
		} while (await WaitAsync(timer, ct));
	}

	private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct) {
		try {
			return await timer.WaitForNextTickAsync(ct);
		}
		catch (OperationCanceledException) {
			return false;
		}
	}

	internal async Task PollOnceAsync(CancellationToken ct) {
		var rpc = engine.State == EngineState.Running ? engine.Rpc : null;
		if (rpc is null) {
			return;
		}
		try {
			var snapshot = await rpc.PollAsync(ct);
			_lastTickFailed = false;
			SnapshotReady?.Invoke(this, snapshot);
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			// Shutting down.
		}
		catch (Exception ex) {
			// Log the first failure of a streak only; a dead engine would otherwise flood the log every second.
			if (!_lastTickFailed) {
				logger.LogWarning(ex, "Polling aria2 failed");
			}
			_lastTickFailed = true;
		}
	}
}
