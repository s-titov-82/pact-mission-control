using System.Text;

namespace Pact.Infrastructure.Updates;

/// <summary>
/// Parses the release pipeline's exact LF-terminated GNU binary checksum format.
/// </summary>
public sealed class ReleaseChecksumManifest
{
	private static readonly UTF8Encoding StrictUtf8 = new(
		encoderShouldEmitUTF8Identifier: false,
		throwOnInvalidBytes: true);
	private readonly IReadOnlyDictionary<string, string> _sha256ByFileName;

	private ReleaseChecksumManifest(IReadOnlyDictionary<string, string> sha256ByFileName)
	{
		_sha256ByFileName = sha256ByFileName;
	}

	/// <summary>
	/// Parses BOM-free UTF-8 bytes whose every line is exactly
	/// <c>&lt;64 hex&gt; *&lt;filename&gt;\n</c>.
	/// </summary>
	/// <param name="utf8">The complete checksum asset bytes.</param>
	/// <exception cref="FormatException">The manifest is empty, ambiguous, or noncanonical.</exception>
	public static ReleaseChecksumManifest Parse(ReadOnlySpan<byte> utf8)
	{
		if (utf8.IsEmpty || utf8[^1] != (byte)'\n')
		{
			throw InvalidManifest();
		}

		Dictionary<string, string> entries = new(StringComparer.Ordinal);
		var lineStart = 0;
		for (var index = 0; index < utf8.Length; index++)
		{
			if (utf8[index] == (byte)'\r')
			{
				throw InvalidManifest();
			}
			if (utf8[index] != (byte)'\n')
			{
				continue;
			}

			ParseLine(utf8[lineStart..index], entries);
			lineStart = index + 1;
		}

		return new ReleaseChecksumManifest(entries);
	}

	/// <summary>Returns the normalized lowercase hash for one exact ordinal asset name.</summary>
	/// <param name="fileName">The exact published asset name.</param>
	/// <exception cref="KeyNotFoundException">The manifest has no entry with that exact name.</exception>
	public string GetRequiredSha256(string fileName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
		return _sha256ByFileName.TryGetValue(fileName, out var sha256)
			? sha256
			: throw new KeyNotFoundException(
				$"The checksum manifest has no entry for '{fileName}'.");
	}

	private static void ParseLine(
		ReadOnlySpan<byte> line,
		Dictionary<string, string> entries)
	{
		if (line.Length < 67 || line[64] != (byte)' ' || line[65] != (byte)'*')
		{
			throw InvalidManifest();
		}

		for (var index = 0; index < 64; index++)
		{
			if (!IsHex(line[index]))
			{
				throw InvalidManifest();
			}
		}

		string fileName;
		try
		{
			fileName = StrictUtf8.GetString(line[66..]);
		}
		catch (DecoderFallbackException)
		{
			throw InvalidManifest();
		}

		if (string.IsNullOrWhiteSpace(fileName)
			|| char.IsWhiteSpace(fileName[0])
			|| char.IsWhiteSpace(fileName[^1])
			|| fileName.Any(character =>
				character is '\0' or '/' or '\\' || char.IsControl(character)))
		{
			throw InvalidManifest();
		}

		var sha256 = Encoding.ASCII.GetString(line[..64]).ToLowerInvariant();
		if (!entries.TryAdd(fileName, sha256))
		{
			throw InvalidManifest();
		}
	}

	private static bool IsHex(byte value) =>
		value is >= (byte)'0' and <= (byte)'9'
			or >= (byte)'a' and <= (byte)'f'
			or >= (byte)'A' and <= (byte)'F';

	private static FormatException InvalidManifest() =>
		new("The checksum manifest is not in the required canonical format.");
}
