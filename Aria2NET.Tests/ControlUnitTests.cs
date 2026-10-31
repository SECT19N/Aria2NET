using System.Net;
using System.Text;
using System.Text.Json;
using Aria2NET.Core.Aria2;
using Aria2NET.Core.Downloads;
using Aria2NET.Core.Settings;

namespace Aria2NET.Tests;

public class ControlUnitTests {
	private static DownloadItem Item(
		DownloadStatus status, string gid = "g", string name = "f.bin", long total = 100, long done = 0, long speed = 0,
		string? uri = "https://x/f.bin", string? path = null, int? code = null) =>
		new(gid, name, status, total, done, speed, null, "C:/dl", uri, path, code);

	[Fact]
	public void Capabilities_follow_status() {
		Assert.True(DownloadCapabilities.CanPause(Item(DownloadStatus.Active)));
		Assert.True(DownloadCapabilities.CanPause(Item(DownloadStatus.Waiting)));
		Assert.False(DownloadCapabilities.CanPause(Item(DownloadStatus.Paused)));
		Assert.True(DownloadCapabilities.CanResume(Item(DownloadStatus.Paused)));
		Assert.False(DownloadCapabilities.CanResume(Item(DownloadStatus.Active)));
		Assert.True(DownloadCapabilities.CanForceStart(Item(DownloadStatus.Waiting)));
		Assert.False(DownloadCapabilities.CanForceStart(Item(DownloadStatus.Paused)));
		Assert.True(DownloadCapabilities.CanMove(Item(DownloadStatus.Paused)));
		Assert.False(DownloadCapabilities.CanMove(Item(DownloadStatus.Active)));
		Assert.True(DownloadCapabilities.CanSetSpeedLimit(Item(DownloadStatus.Active)));
		Assert.False(DownloadCapabilities.CanSetSpeedLimit(Item(DownloadStatus.Complete)));
	}

	[Fact]
	public void Rename_and_folder_change_only_before_any_data_exists() {
		Assert.True(DownloadCapabilities.CanRename(Item(DownloadStatus.Paused, done: 0)));
		Assert.False(DownloadCapabilities.CanRename(Item(DownloadStatus.Paused, done: 10)));
		Assert.False(DownloadCapabilities.CanChangeFolder(Item(DownloadStatus.Active, done: 0)));
		Assert.True(DownloadCapabilities.CanChangeFolder(Item(DownloadStatus.Waiting, done: 0)));
	}

	[Theory]
	[InlineData("https://x/f", true)]
	[InlineData("magnet:?xt=urn:btih:abc", false)]
	[InlineData(null, false)]
	public void Retry_needs_a_plain_address(string? uri, bool expected) =>
		Assert.Equal(expected, DownloadCapabilities.CanRetry(Item(DownloadStatus.Error, uri: uri)));

	[Fact]
	public void Retry_is_only_for_failed_downloads() {
		Assert.False(DownloadCapabilities.CanRetry(Item(DownloadStatus.Active)));
		Assert.False(DownloadCapabilities.CanRetry(Item(DownloadStatus.Complete)));
		Assert.True(DownloadCapabilities.CanRetry(Item(DownloadStatus.Removed)));
	}

	[Fact]
	public void OpenFile_needs_a_completed_file_that_exists() {
		using var temp = new TempDir();
		var file = Path.Combine(temp.Path, "a.txt");
		Assert.False(DownloadCapabilities.CanOpenFile(Item(DownloadStatus.Complete, path: file)));
		File.WriteAllText(file, "x");
		Assert.True(DownloadCapabilities.CanOpenFile(Item(DownloadStatus.Complete, path: file)));
		Assert.False(DownloadCapabilities.CanOpenFile(Item(DownloadStatus.Active, path: file)));
	}

	[Theory]
	[InlineData(3, "File not found on the server")]
	[InlineData(9, "Not enough disk space")]
	[InlineData(24, "Authorization failed")]
	public void Errors_have_friendly_text(int code, string expected) => Assert.Equal(expected, Aria2Errors.Describe(code, "raw"));

	[Fact]
	public void Errors_fall_back_to_aria2_text_then_generic() {
		Assert.Equal("raw", Aria2Errors.Describe(999, "raw"));
		Assert.Equal("raw", Aria2Errors.Describe(null, "raw"));
		Assert.Equal("The download failed", Aria2Errors.Describe(null, null));
	}

	[Fact]
	public void DownloadItem_reads_path_and_error_code() {
		var item = DownloadItem.FromJson(JsonDocument.Parse("""
			{"gid":"g","status":"error","errorCode":"9","errorMessage":"disk","files":[{"path":"C:/dl/a.bin","uris":[]}]}
			""").RootElement);
		Assert.Equal("C:/dl/a.bin", item.Path);
		Assert.Equal(9, item.ErrorCode);

		var none = DownloadItem.FromJson(JsonDocument.Parse("""{"gid":"g","status":"waiting","files":[{"path":""}]}""").RootElement);
		Assert.Null(none.Path);
		Assert.Null(none.ErrorCode);
	}

	private static readonly DownloadItem[] Sample = [
		Item(DownloadStatus.Active, "1", "banana", total: 100, done: 50, speed: 10),
		Item(DownloadStatus.Waiting, "2", "Apple", total: 300),
		Item(DownloadStatus.Complete, "3", "cherry", total: 200, done: 200),
		Item(DownloadStatus.Error, "4", "date", total: 0),
		Item(DownloadStatus.Paused, "5", "elder", total: 50, done: 10),
	];

	[Theory]
	[InlineData(StatusFilter.All, 5)]
	[InlineData(StatusFilter.Downloading, 1)]
	[InlineData(StatusFilter.Waiting, 1)]
	[InlineData(StatusFilter.Paused, 1)]
	[InlineData(StatusFilter.Completed, 1)]
	[InlineData(StatusFilter.Failed, 1)]
	public void Filters_count_matching_items(StatusFilter filter, int expected) {
		Assert.Equal(expected, DownloadView.Count(filter, Sample));
		Assert.Equal(expected, DownloadView.Apply(Sample, filter, SortColumn.None, false).Count);
	}

	[Fact]
	public void No_sort_keeps_aria2_order() =>
		Assert.Equal(["1", "2", "3", "4", "5"], DownloadView.Apply(Sample, StatusFilter.All, SortColumn.None, true).Select(i => i.Gid));

	[Fact]
	public void Sorts_by_name_ignoring_case_and_by_size_and_direction() {
		Assert.Equal(["Apple", "banana", "cherry", "date", "elder"], DownloadView.Apply(Sample, StatusFilter.All, SortColumn.Name, false).Select(i => i.Name));
		Assert.Equal(["2", "3", "1", "5", "4"], DownloadView.Apply(Sample, StatusFilter.All, SortColumn.Size, true).Select(i => i.Gid));
		Assert.Equal("1", DownloadView.Apply(Sample, StatusFilter.All, SortColumn.Speed, true)[0].Gid);
	}

	[Fact]
	public void Sort_is_stable_for_ties() {
		var items = new[] { Item(DownloadStatus.Waiting, "a", total: 5), Item(DownloadStatus.Waiting, "b", total: 5), Item(DownloadStatus.Waiting, "c", total: 1) };
		Assert.Equal(["c", "a", "b"], DownloadView.Apply(items, StatusFilter.All, SortColumn.Size, false).Select(i => i.Gid));
	}

	[Fact]
	public void Old_settings_files_load_with_new_defaults() {
		var s = JsonSerializer.Deserialize("""{"SchemaVersion":1,"LastUsedFolder":"C:/x"}""", AppSettingsJsonContext.Default.AppSettings)!;
		Assert.True(s.ConfirmDeleteFiles);
		Assert.Equal("All", s.StatusFilter);
		Assert.Equal("None", s.SortColumn);
		Assert.False(s.SortDescending);
	}

	private static Aria2RpcClient Client(FakeHandler handler) =>
		new(new HttpClient(handler), new Uri("http://127.0.0.1:1/jsonrpc"), "sekret");

	private static HttpResponseMessage Ok(string body) =>
		new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

	[Fact]
	public async Task Batch_pause_is_one_multicall_with_token_in_each_call() {
		var handler = new FakeHandler((_, _) => Ok("""{"jsonrpc":"2.0","id":"1","result":[["a"],["b"]]}"""));
		var failures = await Client(handler).PauseAsync(["a", "b"]);

		Assert.Empty(failures);
		using var req = JsonDocument.Parse(handler.Bodies.Single());
		Assert.Equal("system.multicall", req.RootElement.GetProperty("method").GetString());
		var calls = req.RootElement.GetProperty("params")[0];
		Assert.Equal(2, calls.GetArrayLength());
		Assert.Equal("aria2.forcePause", calls[0].GetProperty("methodName").GetString());
		Assert.Equal("token:sekret", calls[1].GetProperty("params")[0].GetString());
		Assert.Equal("b", calls[1].GetProperty("params")[1].GetString());
	}

	[Fact]
	public async Task Batch_reports_failed_calls_and_skips_the_request_when_empty() {
		var handler = new FakeHandler((_, _) => Ok("""{"jsonrpc":"2.0","id":"1","result":[["a"],{"code":1,"message":"GID b is not found"}]}"""));
		var client = Client(handler);

		Assert.Equal(["GID b is not found"], await client.RemoveResultAsync(["a", "b"]));
		Assert.Empty(await client.RemoveAsync([]));
		Assert.Single(handler.Bodies);
	}

	[Fact]
	public async Task ChangePosition_sends_gid_offset_and_mode() {
		var handler = new FakeHandler((_, _) => Ok("""{"jsonrpc":"2.0","id":"1","result":2}"""));
		var pos = await Client(handler).ChangePositionAsync("g1", -1, "POS_CUR");

		Assert.Equal(2, pos);
		using var req = JsonDocument.Parse(handler.Bodies.Single());
		Assert.Equal("aria2.changePosition", req.RootElement.GetProperty("method").GetString());
		var p = req.RootElement.GetProperty("params");
		Assert.Equal("g1", p[1].GetString());
		Assert.Equal(-1, p[2].GetInt32());
		Assert.Equal("POS_CUR", p[3].GetString());
	}

	[Fact]
	public async Task ChangeOption_sends_option_map_and_GetOption_reads_it_back() {
		var handler = new FakeHandler((_, body) => body.Contains("getOption")
			? Ok("""{"jsonrpc":"2.0","id":"1","result":{"max-download-limit":"1024","referer":"https://r"}}""")
			: Ok("""{"jsonrpc":"2.0","id":"1","result":"OK"}"""));
		var client = Client(handler);

		await client.ChangeOptionAsync("g", new Dictionary<string, string> { ["out"] = "n.bin" });
		var options = await client.GetOptionAsync("g");

		using var req = JsonDocument.Parse(handler.Bodies[0]);
		Assert.Equal("n.bin", req.RootElement.GetProperty("params")[2].GetProperty("out").GetString());
		Assert.Equal("1024", options["max-download-limit"]);
	}
}
