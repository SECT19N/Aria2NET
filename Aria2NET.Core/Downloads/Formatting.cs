using System.Globalization;

namespace Aria2NET.Core.Downloads;

public static class ByteFormat {
	private static readonly string[] Units = ["B", "KiB", "MiB", "GiB", "TiB"];

	public static string Size(long bytes) {
		if (bytes < 0) {
			return "—";
		}
		double value = bytes;
		var unit = 0;
		while (value >= 1024 && unit < Units.Length - 1) {
			value /= 1024;
			unit++;
		}
		var format = unit == 0 ? "0" : value >= 100 ? "0" : value >= 10 ? "0.0" : "0.00";
		return value.ToString(format, CultureInfo.InvariantCulture) + " " + Units[unit];
	}

	public static string Speed(long bytesPerSec) => Size(bytesPerSec) + "/s";
}

public static class TimeFormat {
	/// <summary>"3m 12s", "1h 05m", "2d 3h"; "--" when unknown.</summary>
	public static string Eta(TimeSpan? eta) {
		if (eta is not { } t || t < TimeSpan.Zero) {
			return "--";
		}
		var seconds = (long)Math.Ceiling(t.TotalSeconds);
		if (seconds < 60) {
			return $"{seconds}s";
		}
		if (seconds < 3600) {
			return $"{seconds / 60}m {seconds % 60:00}s";
		}
		if (seconds < 86400) {
			return $"{seconds / 3600}h {seconds % 3600 / 60:00}m";
		}
		return $"{seconds / 86400}d {seconds % 86400 / 3600}h";
	}
}
