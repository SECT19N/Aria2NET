using System.Text;

namespace Aria2NET.Core.Torrents;

/// <summary>
/// Minimal bencode reader/writer. Values are <see cref="long"/>, <see cref="byte"/>[] (strings are bytes),
/// <see cref="List{Object}"/> and <see cref="Dictionary{String, Object}"/> (keys decoded as Latin-1 so any byte survives).
/// </summary>
public static class Bencode {
	private const int MaxDepth = 64;

	public static object Decode(ReadOnlySpan<byte> data) {
		var pos = 0;
		var value = ReadValue(data, ref pos, 0);
		if (pos != data.Length) {
			throw new FormatException("Trailing data after bencode value.");
		}
		return value;
	}

	/// <summary>Returns the exact bytes of a top-level dictionary entry (an info hash is the SHA-1 of the raw <c>info</c> value, so re-encoding is not safe).</summary>
	public static byte[]? FindRawTopLevelValue(ReadOnlySpan<byte> data, string key) {
		var pos = 0;
		if (data.Length == 0 || data[pos++] != 'd') {
			throw new FormatException("Top-level value is not a dictionary.");
		}
		var wanted = Encoding.Latin1.GetBytes(key);
		while (pos < data.Length && data[pos] != 'e') {
			var k = ReadString(data, ref pos);
			var start = pos;
			ReadValue(data, ref pos, 1);
			if (k.AsSpan().SequenceEqual(wanted)) {
				return data[start..pos].ToArray();
			}
		}
		return null;
	}

	public static byte[] Encode(object value) {
		using var ms = new MemoryStream();
		Write(ms, value);
		return ms.ToArray();
	}

	private static object ReadValue(ReadOnlySpan<byte> data, ref int pos, int depth) {
		if (depth > MaxDepth) {
			throw new FormatException("Bencode nesting too deep.");
		}
		if (pos >= data.Length) {
			throw new FormatException("Unexpected end of bencode data.");
		}
		var c = (char) data[pos];
		switch (c) {
			case 'i': {
					pos++;
					var end = data[pos..].IndexOf((byte) 'e');
					if (end < 0) {
						throw new FormatException("Unterminated integer.");
					}
					var text = Encoding.ASCII.GetString(data.Slice(pos, end));
					if (!long.TryParse(text, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var n)) {
						throw new FormatException("Bad integer.");
					}
					pos += end + 1;
					return n;
				}
			case 'l': {
					pos++;
					var list = new List<object>();
					while (true) {
						if (pos >= data.Length) {
							throw new FormatException("Unterminated list.");
						}
						if (data[pos] == 'e') {
							pos++;
							return list;
						}
						list.Add(ReadValue(data, ref pos, depth + 1));
					}
				}
			case 'd': {
					pos++;
					var dict = new Dictionary<string, object>(StringComparer.Ordinal);
					while (true) {
						if (pos >= data.Length) {
							throw new FormatException("Unterminated dictionary.");
						}
						if (data[pos] == 'e') {
							pos++;
							return dict;
						}
						var key = Encoding.Latin1.GetString(ReadString(data, ref pos));
						dict[key] = ReadValue(data, ref pos, depth + 1);
					}
				}
			default:
				if (c is >= '0' and <= '9') {
					return ReadString(data, ref pos);
				}
				throw new FormatException($"Unexpected byte '{c}' at {pos}.");
		}
	}

	private static byte[] ReadString(ReadOnlySpan<byte> data, ref int pos) {
		var colon = data[pos..].IndexOf((byte) ':');
		if (colon <= 0 || !int.TryParse(Encoding.ASCII.GetString(data.Slice(pos, colon)), out var length) || length < 0) {
			throw new FormatException("Bad string length.");
		}
		pos += colon + 1;
		if (length > data.Length - pos) {
			throw new FormatException("String runs past end of data.");
		}
		var bytes = data.Slice(pos, length).ToArray();
		pos += length;
		return bytes;
	}

	private static void Write(Stream s, object value) {
		switch (value) {
			case long n:
				s.Write(Encoding.ASCII.GetBytes($"i{n}e"));
				break;
			case int n:
				s.Write(Encoding.ASCII.GetBytes($"i{n}e"));
				break;
			case string text:
				WriteBytes(s, Encoding.UTF8.GetBytes(text));
				break;
			case byte[] bytes:
				WriteBytes(s, bytes);
				break;
			case IEnumerable<object> list:
				s.WriteByte((byte) 'l');
				foreach (var item in list) {
					Write(s, item);
				}
				s.WriteByte((byte) 'e');
				break;
			case IDictionary<string, object> dict:
				s.WriteByte((byte) 'd');
				// Keys are sorted by their raw bytes, as the spec requires.
				foreach (var (key, item) in dict.OrderBy(p => p.Key, StringComparer.Ordinal)) {
					WriteBytes(s, Encoding.Latin1.GetBytes(key));
					Write(s, item);
				}
				s.WriteByte((byte) 'e');
				break;
			default:
				throw new ArgumentException($"Cannot bencode {value.GetType().Name}.", nameof(value));
		}
	}

	private static void WriteBytes(Stream s, byte[] bytes) {
		s.Write(Encoding.ASCII.GetBytes($"{bytes.Length}:"));
		s.Write(bytes);
	}
}
