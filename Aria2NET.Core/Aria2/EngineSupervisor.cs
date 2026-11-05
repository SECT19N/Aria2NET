using Microsoft.Extensions.Logging;

namespace Aria2NET.Core.Aria2;

/// <summary>Decides when a crashed engine is restarted: a few tries with growing pauses, then it gives up until the user restarts it.</summary>
public sealed class RestartPolicy(int maxRestarts = 3, TimeSpan? window = null, IReadOnlyList<TimeSpan>? delays = null) {
	private readonly TimeSpan _window = window ?? TimeSpan.FromMinutes(5);
	private readonly IReadOnlyList<TimeSpan> _delays = delays ?? [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10)];
	private readonly Queue<DateTime> _attempts = new();

	/// <summary>How long to wait before the next restart, or null to stop trying (too many restarts inside the window).</summary>
	public TimeSpan? NextDelay(DateTime nowUtc) {
		while (_attempts.Count > 0 && nowUtc - _attempts.Peek() > _window) {
			_attempts.Dequeue();
		}
		if (_attempts.Count >= maxRestarts) {
			return null;
		}
		var delay = _delays[Math.Min(_attempts.Count, _delays.Count - 1)];
		_attempts.Enqueue(nowUtc);
		return delay;
	}

	public void Reset() => _attempts.Clear();
}

/// <summary>Restarts the engine automatically after an unexpected exit, using a <see cref="RestartPolicy"/>.</summary>
public sealed class EngineSupervisor : IDisposable {
	private readonly IAria2Engine _engine;
	private readonly RestartPolicy _policy;
	private readonly ILogger? _logger;
	private readonly Func<TimeSpan, CancellationToken, Task> _delay;
	private readonly Func<DateTime> _clock;
	private readonly CancellationTokenSource _cts = new();
	private readonly Lock _gate = new();
	private bool _busy;

	public EngineSupervisor(IAria2Engine engine, RestartPolicy? policy = null, ILogger? logger = null,
		Func<TimeSpan, CancellationToken, Task>? delay = null, Func<DateTime>? clock = null) {
		_engine = engine;
		_policy = policy ?? new RestartPolicy();
		_logger = logger;
		_delay = delay ?? Task.Delay;
		_clock = clock ?? (() => DateTime.UtcNow);
		_engine.StateChanged += OnStateChanged;
	}

	/// <summary>Raised after an automatic restart brought the engine back. Background thread.</summary>
	public event EventHandler? Restarted;

	/// <summary>Raised when the policy ran out of tries; the engine stays crashed for the user to restart. Background thread.</summary>
	public event EventHandler? GaveUp;

	/// <summary>True from a crash until the engine is back (or the policy gave up). The UI waits before showing an error.</summary>
	public bool IsRecovering {
		get {
			lock (_gate) {
				return _busy;
			}
		}
	}

	public void Dispose() {
		_engine.StateChanged -= OnStateChanged;
		_cts.Cancel();
		_cts.Dispose();
	}

	private void OnStateChanged(object? sender, EventArgs e) {
		if (_engine.State != EngineState.Crashed) {
			return;
		}
		lock (_gate) {
			if (_busy) {
				return;
			}
			_busy = true;
		}
		_ = Task.Run(RunAsync);
	}

	private async Task RunAsync() {
		try {
			while (_engine.State == EngineState.Crashed && !_cts.IsCancellationRequested) {
				var wait = _policy.NextDelay(_clock());
				if (wait is null) {
					_logger?.LogError("aria2 keeps crashing; automatic restart stopped. Last error: {Error}", _engine.LastError);
					lock (_gate) {
						_busy = false; // before the event, so a listener asking IsRecovering sees the truth
					}
					GaveUp?.Invoke(this, EventArgs.Empty);
					return;
				}
				_logger?.LogWarning("aria2 crashed ({Error}); restarting in {Delay}", _engine.LastError, wait.Value);
				await _delay(wait.Value, _cts.Token);
				if (_engine.State != EngineState.Crashed) {
					return; // the user restarted it meanwhile
				}
				await _engine.RestartAsync(_cts.Token);
				if (_engine.State == EngineState.Running) {
					Restarted?.Invoke(this, EventArgs.Empty);
					return;
				}
			}
		}
		catch (OperationCanceledException) {
			// Shutting down.
		}
		catch (Exception ex) {
			_logger?.LogError(ex, "Automatic restart failed");
		}
		finally {
			lock (_gate) {
				_busy = false;
			}
		}
	}
}
