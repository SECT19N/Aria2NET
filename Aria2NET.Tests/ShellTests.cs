using System.Runtime.Versioning;
using Aria2NET.Core.Shell;
using Microsoft.Win32;

namespace Aria2NET.Tests;

public class LaunchArgumentsTests {
	private const string Dir = @"C:\Users\me\Downloads";

	[Fact]
	public void Picks_magnets_and_torrent_files_and_ignores_the_rest() {
		var result = LaunchArguments.Extract([@"C:\x\a.torrent", "magnet:?xt=urn:btih:abc", "--minimized", @"C:\x\notes.txt", "https://example.com/a.zip", @"C:\x\b.META4"]);

		Assert.Equal([@"C:\x\a.torrent", "magnet:?xt=urn:btih:abc", @"C:\x\b.META4"], result);
	}

	[Fact]
	public void Resolves_relative_file_names_against_the_working_folder() {
		Assert.Equal([Path.Combine(Dir, "a.torrent"), Path.Combine(Dir, "sub", "b.torrent")], LaunchArguments.Extract(["a.torrent", @"sub\b.torrent"], Dir));
	}

	[Fact]
	public void Drops_duplicates_and_blanks() {
		Assert.Equal([@"C:\a.torrent"], LaunchArguments.Extract([@"C:\a.torrent", "  ", "", @"C:\a.torrent"]));
	}

	[Fact]
	public void A_magnet_is_kept_exactly_as_given() {
		var magnet = "magnet:?xt=urn:btih:abc&dn=My%20File&tr=udp%3A%2F%2Ft%3A1";

		Assert.Equal([magnet], LaunchArguments.Extract([magnet], Dir));
	}

	[Theory]
	[InlineData("", new string[0])]
	[InlineData("   ", new string[0])]
	[InlineData(@"a b c", new[] { "a", "b", "c" })]
	[InlineData(@"""C:\My Files\a.torrent""", new[] { @"C:\My Files\a.torrent" })]
	[InlineData(@"--flag ""C:\My Files\a.torrent"" magnet:?xt=urn:btih:abc", new[] { "--flag", @"C:\My Files\a.torrent", "magnet:?xt=urn:btih:abc" })]
	[InlineData(@"""a \""quoted\"" word""", new[] { @"a ""quoted"" word" })]
	[InlineData(@"C:\dir\\ x", new[] { @"C:\dir\\", "x" })]
	[InlineData(@"""C:\dir\\"" x", new[] { @"C:\dir\", "x" })]
	[InlineData(@"""""", new[] { "" })]
	public void Splits_a_command_line_like_windows(string commandLine, string[] expected) {
		Assert.Equal(expected, LaunchArguments.Split(commandLine));
	}
}

[SupportedOSPlatform("windows")]
public sealed class ShellRegistrationTests : IDisposable {
	private const string Exe = @"C:\Program Files\Aria2NET\Aria2NET.exe";
	private readonly string _path = @"Software\Aria2NET-Tests\" + Guid.NewGuid().ToString("N");
	private readonly RegistryKey _software;

	public ShellRegistrationTests() {
		_software = Registry.CurrentUser.CreateSubKey(_path, writable: true);
	}

	public void Dispose() {
		_software.Dispose();
		Registry.CurrentUser.DeleteSubKeyTree(_path, throwOnMissingSubKey: false);
	}

	private ShellRegistration Create(string exe = Exe) => new(exe, _software, notifyShell: false);

	[Fact]
	public void Nothing_is_registered_at_first() {
		Assert.Equal(new ShellRegistrationState(false, false, false), Create().GetState());
	}

	[Fact]
	public void Register_adds_the_app_as_a_candidate_for_files_and_magnet_links() {
		Create().Register();

		Assert.True(Create().GetState().Registered);
		using var command = _software.OpenSubKey($@"Classes\{ShellRegistration.TorrentProgId}\shell\open\command")!;
		Assert.Equal($"\"{Exe}\" \"%1\"", command.GetValue(""));
		using var magnet = _software.OpenSubKey($@"Classes\{ShellRegistration.MagnetProgId}")!;
		Assert.NotNull(magnet.GetValue("URL Protocol"));
		foreach (var extension in new[] { ".torrent", ".metalink", ".meta4" }) {
			using var with = _software.OpenSubKey($@"Classes\{extension}\OpenWithProgids")!;
			Assert.Contains(with.GetValueNames(), n => n.StartsWith("Aria2NET.", StringComparison.Ordinal));
		}
		using var caps = _software.OpenSubKey(@"Aria2NET\Capabilities\URLAssociations")!;
		Assert.Equal(ShellRegistration.MagnetProgId, caps.GetValue("magnet"));
		using var registered = _software.OpenSubKey("RegisteredApplications")!;
		Assert.Equal(@"Software\Aria2NET\Capabilities", registered.GetValue("Aria2NET"));
	}

	[Fact]
	public void Register_never_takes_over_an_existing_default() {
		using (var ext = _software.CreateSubKey(@"Classes\.torrent", writable: true)) {
			ext.SetValue("", "OtherClient.Torrent");
		}

		Create().Register();

		using var after = _software.OpenSubKey(@"Classes\.torrent")!;
		Assert.Equal("OtherClient.Torrent", after.GetValue(""));
	}

	[Fact]
	public void Unregister_removes_what_Register_added_and_leaves_other_clients_alone() {
		using (var with = _software.CreateSubKey(@"Classes\.torrent\OpenWithProgids", writable: true)) {
			with.SetValue("OtherClient.Torrent", Array.Empty<byte>(), RegistryValueKind.None);
		}
		var registration = Create();
		registration.Register();

		registration.Unregister();

		Assert.Equal(new ShellRegistrationState(false, false, false), registration.GetState());
		Assert.Null(_software.OpenSubKey($@"Classes\{ShellRegistration.TorrentProgId}"));
		Assert.Null(_software.OpenSubKey("Aria2NET"));
		using var registered = _software.OpenSubKey("RegisteredApplications");
		Assert.Null(registered?.GetValue("Aria2NET"));
		using var remaining = _software.OpenSubKey(@"Classes\.torrent\OpenWithProgids")!;
		Assert.Equal(["OtherClient.Torrent"], remaining.GetValueNames());
		registration.Unregister(); // nothing left: must not throw
	}

	[Fact]
	public void A_registration_for_a_moved_exe_counts_as_not_registered() {
		Create(@"C:\Old\Aria2NET.exe").Register();

		Assert.False(Create(@"C:\New\Aria2NET.exe").GetState().Registered);
	}

	[Fact]
	public void Reports_when_the_user_picked_the_app_as_default() {
		Create().Register();
		using (var choice = _software.CreateSubKey(@"Microsoft\Windows\CurrentVersion\Explorer\FileExts\.torrent\UserChoice", writable: true)) {
			choice.SetValue("ProgId", ShellRegistration.TorrentProgId);
		}

		var state = Create().GetState();

		Assert.True(state.DefaultForTorrent);
		Assert.False(state.DefaultForMagnet);
		using (var choice = _software.CreateSubKey(@"Microsoft\Windows\Shell\Associations\UrlAssociations\magnet\UserChoice", writable: true)) {
			choice.SetValue("ProgId", "SomeoneElse.Magnet");
		}
		Assert.False(Create().GetState().DefaultForMagnet);
	}

	[Fact]
	public void Register_twice_is_harmless() {
		var registration = Create();
		registration.Register();
		registration.Register();

		Assert.True(registration.GetState().Registered);
	}
}
