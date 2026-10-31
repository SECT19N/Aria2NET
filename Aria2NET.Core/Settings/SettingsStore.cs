using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;

namespace Aria2NET.Core.Settings;

/// <summary>JSON settings file with atomic writes and recovery from a corrupt file.</summary>
public sealed class SettingsStore<T>(string path, JsonTypeInfo<T> typeInfo, ILogger? logger = null) where T : class, new() {
	private readonly string _path = path;
	private readonly JsonTypeInfo<T> _typeInfo = typeInfo;
	private readonly ILogger? _logger = logger;

	public T Load() {
		if (!File.Exists(_path)) {
			return new T();
		}
		try {
			using var stream = File.OpenRead(_path);
			return JsonSerializer.Deserialize(stream, _typeInfo) ?? new T();
		}
		catch (Exception ex) when (ex is JsonException or IOException) {
			var backup = $"{_path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}.bak";
			_logger?.LogWarning(ex, "Settings file unreadable; moving it to {Backup} and using defaults", backup);
			try {
				File.Move(_path, backup, overwrite: true);
			}
			catch (IOException) {
				// Best effort; defaults still apply.
			}
			return new T();
		}
	}

	public void Save(T value) {
		Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
		var temp = _path + ".tmp";
		using (var stream = File.Create(temp)) {
			JsonSerializer.Serialize(stream, value, _typeInfo);
		}
		File.Move(temp, _path, overwrite: true);
	}
}
