using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;

namespace Aria2NET.Core.Torrents;

/// <summary>
/// Country of an IP address from an offline "IP to Country" CSV (<c>start,end,CC</c> per line, as in DB-IP's free Lite database,
/// CC BY 4.0). Nothing is ever sent anywhere. Without the file every lookup simply returns null.
/// </summary>
public sealed class GeoIpResolver {
	private readonly uint[] _v4Start;
	private readonly uint[] _v4End;
	private readonly string[] _v4Country;
	private readonly UInt128[] _v6Start;
	private readonly UInt128[] _v6End;
	private readonly string[] _v6Country;

	public static GeoIpResolver Empty { get; } = new([], [], [], [], [], []);

	public int RangeCount => _v4Start.Length + _v6Start.Length;

	private GeoIpResolver(uint[] v4Start, uint[] v4End, string[] v4Country, UInt128[] v6Start, UInt128[] v6End, string[] v6Country) {
		_v4Start = v4Start;
		_v4End = v4End;
		_v4Country = v4Country;
		_v6Start = v6Start;
		_v6End = v6End;
		_v6Country = v6Country;
	}

	/// <summary>Reads a CSV file (plain or gzip). A missing or unreadable file gives <see cref="Empty"/>.</summary>
	public static GeoIpResolver Load(string path) {
		try {
			if (!File.Exists(path)) {
				return Empty;
			}
			using var file = File.OpenRead(path);
			using var stream = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? new GZipStream(file, CompressionMode.Decompress) : (Stream)file;
			using var reader = new StreamReader(stream);
			return FromCsv(reader);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) {
			return Empty;
		}
	}

	public static GeoIpResolver FromCsv(TextReader reader) {
		var v4 = new List<(uint Start, uint End, string Country)>();
		var v6 = new List<(UInt128 Start, UInt128 End, string Country)>();
		string? line;
		while ((line = reader.ReadLine()) is not null) {
			var parts = line.Split(',').Select(p => p.Trim().Trim('"')).ToArray();
			if (parts.Length < 3 || parts[2].Length != 2 || !IPAddress.TryParse(parts[0], out var start) || !IPAddress.TryParse(parts[1], out var end)) {
				continue;
			}
			var country = parts[2].ToUpperInvariant();
			if (start.AddressFamily == AddressFamily.InterNetwork && end.AddressFamily == AddressFamily.InterNetwork) {
				v4.Add((ToUInt32(start), ToUInt32(end), country));
			}
			else if (start.AddressFamily == AddressFamily.InterNetworkV6 && end.AddressFamily == AddressFamily.InterNetworkV6) {
				v6.Add((ToUInt128(start), ToUInt128(end), country));
			}
		}
		v4.Sort((a, b) => a.Start.CompareTo(b.Start));
		v6.Sort((a, b) => a.Start.CompareTo(b.Start));
		return new GeoIpResolver(
			[.. v4.Select(r => r.Start)], [.. v4.Select(r => r.End)], [.. v4.Select(r => r.Country)],
			[.. v6.Select(r => r.Start)], [.. v6.Select(r => r.End)], [.. v6.Select(r => r.Country)]);
	}

	/// <summary>Two-letter country code, or null when unknown (or the address is not an address).</summary>
	public string? Lookup(string? ip) {
		if (RangeCount == 0 || !IPAddress.TryParse(ip, out var address)) {
			return null;
		}
		if (address.IsIPv4MappedToIPv6) {
			address = address.MapToIPv4();
		}
		if (address.AddressFamily == AddressFamily.InterNetwork) {
			var value = ToUInt32(address);
			var i = Array.BinarySearch(_v4Start, value);
			if (i < 0) {
				i = ~i - 1;
			}
			return i >= 0 && value <= _v4End[i] ? _v4Country[i] : null;
		}
		if (address.AddressFamily == AddressFamily.InterNetworkV6) {
			var value = ToUInt128(address);
			var i = Array.BinarySearch(_v6Start, value);
			if (i < 0) {
				i = ~i - 1;
			}
			return i >= 0 && value <= _v6End[i] ? _v6Country[i] : null;
		}
		return null;
	}

	private static uint ToUInt32(IPAddress a) {
		var b = a.GetAddressBytes();
		return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
	}

	private static UInt128 ToUInt128(IPAddress a) {
		var b = a.GetAddressBytes();
		UInt128 value = 0;
		foreach (var x in b) {
			value = (value << 8) | x;
		}
		return value;
	}

	/// <summary>Plain-text name of a country code, for tooltips; the code itself when it is not one we know.</summary>
	public static string CountryName(string? code) {
		if (string.IsNullOrEmpty(code)) {
			return "";
		}
		try {
			return new RegionInfo(code).EnglishName;
		}
		catch (ArgumentException) {
			return code;
		}
	}
}
