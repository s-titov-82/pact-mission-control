using System.Globalization;

namespace Pact.Core.Updates;

/// <summary>
/// Identifies a stable release by its three nonnegative numeric components.
/// </summary>
/// <param name="Major">The major release component.</param>
/// <param name="Minor">The minor release component.</param>
/// <param name="Patch">The patch release component.</param>
public readonly record struct StableReleaseVersion(int Major, int Minor, int Patch)
	: IComparable<StableReleaseVersion>
{
	/// <summary>
	/// Parses an exact GitHub release tag in the form <c>vMAJOR.MINOR.PATCH</c>.
	/// </summary>
	/// <param name="value">The complete tag value.</param>
	/// <param name="version">Receives the parsed version on success.</param>
	/// <returns><see langword="true"/> only for a complete stable tag.</returns>
	public static bool TryParseTag(
		string? value,
		out StableReleaseVersion version)
	{
		version = default;
		return value is { Length: > 1 }
			&& value[0] == 'v'
			&& TryParseCore(value.AsSpan(1), out version);
	}

	/// <summary>
	/// Parses a stable assembly informational version and ignores valid build metadata.
	/// </summary>
	/// <param name="value">The informational version without a leading <c>v</c>.</param>
	/// <param name="version">Receives the parsed version on success.</param>
	/// <returns>
	/// <see langword="true"/> for <c>MAJOR.MINOR.PATCH</c> with an optional valid
	/// <c>+build.metadata</c> suffix; otherwise <see langword="false"/>.
	/// </returns>
	public static bool TryParseInformationalVersion(
		string? value,
		out StableReleaseVersion version)
	{
		version = default;
		if (string.IsNullOrEmpty(value))
		{
			return false;
		}

		ReadOnlySpan<char> candidate = value.AsSpan();
		var metadataSeparator = candidate.IndexOf('+');
		if (metadataSeparator >= 0)
		{
			var metadata = candidate[(metadataSeparator + 1)..];
			if (!IsValidBuildMetadata(metadata))
			{
				return false;
			}

			candidate = candidate[..metadataSeparator];
		}

		return TryParseCore(candidate, out version);
	}

	/// <inheritdoc />
	public int CompareTo(StableReleaseVersion other)
	{
		var result = Major.CompareTo(other.Major);
		if (result != 0)
		{
			return result;
		}

		result = Minor.CompareTo(other.Minor);
		return result != 0 ? result : Patch.CompareTo(other.Patch);
	}

	/// <summary>Determines whether one release precedes another.</summary>
	public static bool operator <(
		StableReleaseVersion left,
		StableReleaseVersion right) => left.CompareTo(right) < 0;

	/// <summary>Determines whether one release does not follow another.</summary>
	public static bool operator <=(
		StableReleaseVersion left,
		StableReleaseVersion right) => left.CompareTo(right) <= 0;

	/// <summary>Determines whether one release follows another.</summary>
	public static bool operator >(
		StableReleaseVersion left,
		StableReleaseVersion right) => left.CompareTo(right) > 0;

	/// <summary>Determines whether one release does not precede another.</summary>
	public static bool operator >=(
		StableReleaseVersion left,
		StableReleaseVersion right) => left.CompareTo(right) >= 0;

	/// <summary>
	/// Formats the numeric version without a tag prefix or metadata.
	/// </summary>
	/// <returns>The invariant <c>MAJOR.MINOR.PATCH</c> representation.</returns>
	public override string ToString() =>
		string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

	private static bool TryParseCore(
		ReadOnlySpan<char> value,
		out StableReleaseVersion version)
	{
		version = default;
		var firstDot = value.IndexOf('.');
		if (firstDot <= 0)
		{
			return false;
		}

		var remainder = value[(firstDot + 1)..];
		var secondDot = remainder.IndexOf('.');
		if (secondDot <= 0 || remainder[(secondDot + 1)..].Contains('.'))
		{
			return false;
		}

		var majorText = value[..firstDot];
		var minorText = remainder[..secondDot];
		var patchText = remainder[(secondDot + 1)..];
		if (!TryParseComponent(majorText, out var major)
			|| !TryParseComponent(minorText, out var minor)
			|| !TryParseComponent(patchText, out var patch))
		{
			return false;
		}

		version = new StableReleaseVersion(major, minor, patch);
		return true;
	}

	private static bool TryParseComponent(ReadOnlySpan<char> value, out int result)
	{
		result = default;
		if (value.IsEmpty)
		{
			return false;
		}

		foreach (var character in value)
		{
			if (character is < '0' or > '9')
			{
				return false;
			}
		}

		return int.TryParse(
			value,
			NumberStyles.None,
			CultureInfo.InvariantCulture,
			out result);
	}

	private static bool IsValidBuildMetadata(ReadOnlySpan<char> value)
	{
		if (value.IsEmpty || value[0] == '.' || value[^1] == '.')
		{
			return false;
		}

		var previousWasDot = false;
		foreach (var character in value)
		{
			if (character == '.')
			{
				if (previousWasDot)
				{
					return false;
				}

				previousWasDot = true;
				continue;
			}

			if (!IsAsciiLetterOrDigit(character) && character != '-')
			{
				return false;
			}

			previousWasDot = false;
		}

		return true;
	}

	private static bool IsAsciiLetterOrDigit(char character) =>
		character is >= '0' and <= '9'
			or >= 'A' and <= 'Z'
			or >= 'a' and <= 'z';
}
