using System.Text;
using Pact.Infrastructure.Updates;

namespace Pact.Infrastructure.Tests.Updates;

public sealed class ReleaseChecksumManifestTests
{
	private const string LowerHash =
		"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
	private const string UpperHash =
		"ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789";

	[Test]
	public void Parse_accepts_exact_lf_terminated_gnu_binary_lines_and_normalizes_hashes()
	{
		ReleaseChecksumManifest manifest = Parse(
			$"{LowerHash} *setup.exe\n{UpperHash} *SHA256SUMS.txt\n");

		manifest.GetRequiredSha256("setup.exe").ShouldBe(LowerHash);
		manifest.GetRequiredSha256("SHA256SUMS.txt").ShouldBe(UpperHash.ToLowerInvariant());
	}

	[Test]
	public void Lookup_is_exact_ordinal_and_missing_entry_fails()
	{
		ReleaseChecksumManifest manifest = Parse($"{LowerHash} *Setup.exe\n");

		Should.Throw<KeyNotFoundException>(() => manifest.GetRequiredSha256("setup.exe"));
		manifest.GetRequiredSha256("Setup.exe").ShouldBe(LowerHash);
	}

	[TestCase("")]
	[TestCase("hash *setup.exe\n")]
	[TestCase("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg *setup.exe\n")]
	[TestCase("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  *setup.exe\n")]
	[TestCase("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef setup.exe\n")]
	[TestCase("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef * setup.exe\n")]
	[TestCase("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef *setup.exe")]
	[TestCase("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef *setup.exe\r\n")]
	[TestCase("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef *setup.exe\r")]
	[TestCase("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef *folder/setup.exe\n")]
	[TestCase("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef *folder\\setup.exe\n")]
	[TestCase("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef *\n")]
	public void Parse_rejects_noncanonical_input(string text) =>
		Should.Throw<FormatException>(() => Parse(text));

	[Test]
	public void Parse_rejects_bom_nul_invalid_utf8_and_duplicate_names()
	{
		byte[] canonical = Encoding.UTF8.GetBytes($"{LowerHash} *setup.exe\n");
		Should.Throw<FormatException>(() => ReleaseChecksumManifest.Parse(
			[0xef, 0xbb, 0xbf, .. canonical]));
		Should.Throw<FormatException>(() => Parse($"{LowerHash} *setup\0.exe\n"));
		Should.Throw<FormatException>(() => ReleaseChecksumManifest.Parse(
			[.. Encoding.ASCII.GetBytes($"{LowerHash} *setup"), 0xff, (byte)'\n']));
		Should.Throw<FormatException>(() => Parse(
			$"{LowerHash} *setup.exe\n{UpperHash} *setup.exe\n"));
	}

	private static ReleaseChecksumManifest Parse(string text) =>
		ReleaseChecksumManifest.Parse(Encoding.UTF8.GetBytes(text));
}
