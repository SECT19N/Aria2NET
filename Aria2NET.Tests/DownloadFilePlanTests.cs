using System.Text.Json;
using Aria2NET.Core.Downloads;

namespace Aria2NET.Tests;

public class DownloadFilePlanTests {
	private static string Dir => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dl");

	[Fact]
	public void A_multi_file_torrent_owns_its_folder_and_the_control_file_beside_it() {
		DownloadFilePlan plan = DownloadFilePlan.Build(Dir, [System.IO.Path.Combine(Dir, "Show", "a.mkv"), System.IO.Path.Combine(Dir, "Show", "sub", "b.mkv")], "Show");

		Assert.Equal(2, plan.Files.Count);
		Assert.Equal(System.IO.Path.Combine(Dir, "Show"), plan.Folder);
		Assert.Contains(System.IO.Path.Combine(Dir, "Show.aria2"), plan.ControlFiles);
	}

	[Fact]
	public void A_single_file_has_just_its_own_control_file() {
		string file = System.IO.Path.Combine(Dir, "a.zip");
		DownloadFilePlan plan = DownloadFilePlan.Build(Dir, [file], null);

		Assert.Null(plan.Folder);
		Assert.Equal([file + ".aria2"], plan.ControlFiles);
	}

	[Fact]
	public void Paths_outside_the_download_folder_are_ignored() {
		DownloadFilePlan plan = DownloadFilePlan.Build(Dir, [System.IO.Path.Combine(Dir, "..", "secret.txt"), Dir + "/ok.bin"], "..");

		Assert.Equal([System.IO.Path.Combine(Dir, "ok.bin")], plan.Files);
		Assert.Null(plan.Folder);
	}

	[Fact]
	public void The_plan_is_read_from_aria2_status_and_skips_magnet_metadata() {
		string dir = Dir.Replace('\\', '/');
		string json = "{\"dir\":\"" + dir + "\",\"files\":[{\"path\":\"" + dir + "/Show/a.mkv\"},{\"path\":\"" + dir + "/Show/b.mkv\"}],\"bittorrent\":{\"info\":{\"name\":\"Show\"}}}";
		using JsonDocument doc = JsonDocument.Parse(json);
		DownloadFilePlan plan = DownloadFilePlan.FromStatus(doc.RootElement);

		Assert.Equal(2, plan.Files.Count);
		Assert.Equal(System.IO.Path.Combine(Dir, "Show"), plan.Folder);

		using JsonDocument magnet = JsonDocument.Parse("{\"dir\":\"" + dir + "\",\"files\":[{\"path\":\"[METADATA]Show\"}]}");
		Assert.Empty(DownloadFilePlan.FromStatus(magnet.RootElement).Files);
	}
}
