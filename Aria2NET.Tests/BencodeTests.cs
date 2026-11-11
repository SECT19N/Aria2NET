using System.Security.Cryptography;
using System.Text;
using Aria2NET.Core.Torrents;

namespace Aria2NET.Tests;

public class BencodeTests {
	private static byte[] Bytes(string s) => Encoding.Latin1.GetBytes(s);

	[Fact]
	public void Decodes_integers_strings_lists_and_dictionaries() {
		var value = (Dictionary<string, object>) Bencode.Decode(Bytes("d3:bari-42e3:fool1:a2:bcee"));

		Assert.Equal(-42L, value["bar"]);
		var list = (List<object>) value["foo"];
		Assert.Equal(Bytes("a"), (byte[]) list[0]);
		Assert.Equal(Bytes("bc"), (byte[]) list[1]);
	}

	[Fact]
	public void Round_trips_a_nested_value_with_sorted_keys() {
		var dict = new Dictionary<string, object> {
			["z"] = 1L,
			["a"] = new List<object> { "x", 2L },
		};

		var encoded = Bencode.Encode(dict);

		Assert.Equal("d1:al1:xi2ee1:zi1ee", Encoding.ASCII.GetString(encoded));
		var back = (Dictionary<string, object>) Bencode.Decode(encoded);
		Assert.Equal(1L, back["z"]);
	}

	[Theory]
	[InlineData("")]
	[InlineData("i12")]
	[InlineData("ie")]
	[InlineData("5:abc")]
	[InlineData("l1:a")]
	[InlineData("d1:a")]
	[InlineData("i1ex")]
	[InlineData("x")]
	public void Rejects_malformed_input(string text) {
		Assert.Throws<FormatException>(() => Bencode.Decode(Bytes(text)));
	}

	[Fact]
	public void Rejects_runaway_nesting() {
		Assert.Throws<FormatException>(() => Bencode.Decode(Bytes(new string('l', 200) + new string('e', 200))));
	}

	[Fact]
	public void Finds_the_raw_info_bytes_so_the_hash_does_not_depend_on_re_encoding() {
		// Keys deliberately out of order inside info: re-encoding would change the bytes and the hash.
		var info = "d6:lengthi3e4:name1:ae";
		var info2 = "d4:name1:a6:lengthi3ee";
		var file = Bytes("d8:announce4:http4:info" + info2 + "e");

		var raw = Bencode.FindRawTopLevelValue(file, "info");

		Assert.NotNull(raw);
		Assert.Equal(Bytes(info2), raw);
		Assert.NotEqual(Convert.ToHexString(SHA1.HashData(Bytes(info))), Convert.ToHexString(SHA1.HashData(raw!)));
	}

	[Fact]
	public void Raw_lookup_returns_null_when_the_key_is_absent() {
		Assert.Null(Bencode.FindRawTopLevelValue(Bytes("d1:ai1ee"), "info"));
	}
}
