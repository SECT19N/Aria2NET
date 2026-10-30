using System.Text;
using Microsoft.Extensions.Logging;

namespace Aria2NET.Core.Logging;

/// <summary>Appends log lines to a daily file (aria2net-yyyyMMdd.log) and prunes old files.</summary>
public sealed class FileLoggerProvider : ILoggerProvider {
	private readonly string _dir;
	private readonly object _lock = new();
	private readonly LogLevel _minLevel;

	public FileLoggerProvider(string logsDir, LogLevel minLevel = LogLevel.Information, int retainDays = 14) {
		_dir = logsDir;
		_minLevel = minLevel;
		try {
			Directory.CreateDirectory(_dir);
			var cutoff = DateTime.UtcNow.AddDays(-retainDays);
			foreach (var file in Directory.EnumerateFiles(_dir, "aria2net-*.log")) {
				if (File.GetLastWriteTimeUtc(file) < cutoff) {
					File.Delete(file);
				}
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			// Logging must never take the app down.
		}
	}

	public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

	public void Dispose() { }

	private void Write(string category, LogLevel level, string message, Exception? exception) {
		var sb = new StringBuilder();
		sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
			.Append(" [").Append(level.ToString()[..3].ToUpperInvariant()).Append("] ")
			.Append(category).Append(": ").Append(message);
		if (exception is not null) {
			sb.AppendLine().Append(exception);
		}
		sb.AppendLine();

		var path = Path.Combine(_dir, $"aria2net-{DateTime.Now:yyyyMMdd}.log");
		lock (_lock) {
			try {
				File.AppendAllText(path, sb.ToString());
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
				// Drop the line rather than crash.
			}
		}
	}

	private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger {
		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

		public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= owner._minLevel;

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
			if (IsEnabled(logLevel)) {
				owner.Write(category, logLevel, formatter(state, exception), exception);
			}
		}
	}
}
