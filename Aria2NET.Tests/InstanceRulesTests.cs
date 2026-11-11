using Aria2NET.Core.Shell;

namespace Aria2NET.Tests;

public class InstanceRulesTests {
	[Theory]
	[InlineData("0.8.0", 0, 8, 0)]
	[InlineData("0.8.0.0", 0, 8, 0)]
	[InlineData("0.6.1+abc123", 0, 6, 1)]
	[InlineData("1.0.0-rc.1", 1, 0, 0)]
	[InlineData(" 2.3 ", 2, 3, 0)]
	public void Versions_are_read_without_build_metadata_or_pre_release_suffix(string text, int major, int minor, int patch) =>
		Assert.Equal(new Version(major, minor, patch), InstanceRules.ParseVersion(text));

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("garbage")]
	[InlineData("1")]
	public void Unreadable_versions_are_null(string? text) => Assert.Null(InstanceRules.ParseVersion(text));

	[Theory]
	[InlineData("0.6.1.0", "0.8.0", true)]   // the case that bit us: an old copy in the tray, a new one launched
	[InlineData("0.8.0.0", "0.8.0", false)]  // same version: hand over as before
	[InlineData("0.9.0.0", "0.8.0", false)]  // never close a newer copy
	[InlineData("0.8.0.0", "0.8.1", true)]
	[InlineData(null, "0.8.0", true)]        // unreadable counts as older
	[InlineData("garbage", "0.8.0", true)]
	public void A_newer_copy_replaces_only_an_older_running_one(string? running, string mine, bool replace) =>
		Assert.Equal(replace, InstanceRules.RunningCopyIsOlder(running, Version.Parse(mine)));
}
