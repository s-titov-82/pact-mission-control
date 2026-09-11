using Pact.Core.Updates;

namespace Pact.Core.Tests.Updates;

public sealed class StableReleaseVersionTests
{
	[TestCase("v0.0.0", 0, 0, 0)]
	[TestCase("v1.2.3", 1, 2, 3)]
	[TestCase("v01.002.0003", 1, 2, 3)]
	public void TryParseTag_accepts_only_complete_stable_tags(
		string value,
		int major,
		int minor,
		int patch)
	{
		StableReleaseVersion.TryParseTag(value, out var parsed).ShouldBeTrue();
		parsed.ShouldBe(new StableReleaseVersion(major, minor, patch));
	}

	[TestCase(null)]
	[TestCase("")]
	[TestCase("1.2.3")]
	[TestCase("v1.2")]
	[TestCase("v1.2.3.4")]
	[TestCase("v1.2.3-alpha")]
	[TestCase("v1.2.3+sha")]
	[TestCase(" v1.2.3")]
	[TestCase("v1.2.3 ")]
	[TestCase("release-v1.2.3")]
	[TestCase("v2147483648.0.0")]
	[TestCase("v1.-2.3")]
	public void TryParseTag_rejects_every_noncanonical_shape(string? value)
	{
		StableReleaseVersion.TryParseTag(value, out _).ShouldBeFalse();
	}

	[TestCase("0.0.0", 0, 0, 0)]
	[TestCase("1.2.3", 1, 2, 3)]
	[TestCase("1.2.3+sha", 1, 2, 3)]
	[TestCase("1.2.3+sha.42-win-x64", 1, 2, 3)]
	public void TryParseInformationalVersion_accepts_stable_core_and_build_metadata(
		string value,
		int major,
		int minor,
		int patch)
	{
		StableReleaseVersion.TryParseInformationalVersion(value, out var parsed)
			.ShouldBeTrue();
		parsed.ShouldBe(new StableReleaseVersion(major, minor, patch));
	}

	[TestCase(null)]
	[TestCase("")]
	[TestCase("v1.2.3")]
	[TestCase("1.2")]
	[TestCase("1.2.3.4")]
	[TestCase("1.2.3-alpha")]
	[TestCase("1.2.3+")]
	[TestCase("1.2.3+sha..42")]
	[TestCase("1.2.3+sha_42")]
	[TestCase(" 1.2.3")]
	[TestCase("1.2.3 ")]
	[TestCase("2147483648.0.0")]
	public void TryParseInformationalVersion_rejects_nonstable_or_malformed_values(
		string? value)
	{
		StableReleaseVersion.TryParseInformationalVersion(value, out _).ShouldBeFalse();
	}

	[Test]
	public void Comparison_is_ordinal_by_major_minor_and_patch()
	{
		var version = new StableReleaseVersion(1, 2, 3);

		version.CompareTo(new StableReleaseVersion(1, 2, 3)).ShouldBe(0);
		version.CompareTo(new StableReleaseVersion(1, 2, 4)).ShouldBeLessThan(0);
		version.CompareTo(new StableReleaseVersion(1, 3, 0)).ShouldBeLessThan(0);
		version.CompareTo(new StableReleaseVersion(2, 0, 0)).ShouldBeLessThan(0);
		version.CompareTo(new StableReleaseVersion(1, 2, 2)).ShouldBeGreaterThan(0);
		version.CompareTo(new StableReleaseVersion(1, 1, 99)).ShouldBeGreaterThan(0);
		version.CompareTo(new StableReleaseVersion(0, 99, 99)).ShouldBeGreaterThan(0);
		(version == new StableReleaseVersion(1, 2, 3)).ShouldBeTrue();
		(version <= new StableReleaseVersion(1, 2, 3)).ShouldBeTrue();
		(version >= new StableReleaseVersion(1, 2, 3)).ShouldBeTrue();
		(version < new StableReleaseVersion(1, 2, 4)).ShouldBeTrue();
		(version > new StableReleaseVersion(1, 2, 2)).ShouldBeTrue();
	}

	[Test]
	public void ToString_uses_invariant_ordinal_components()
	{
		new StableReleaseVersion(12, 34, 56).ToString().ShouldBe("12.34.56");
	}
}
