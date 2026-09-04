namespace Pact.Core.Presentation;

/// <summary>
/// Validated UTF-16 range used to read a bounded slice of a browser document.
/// </summary>
public readonly record struct WebPageDocumentRange
{
	/// <summary>Default maximum number of UTF-16 code units returned by one read.</summary>
	public const int DefaultMaxChars = 100_000;

	/// <summary>Hard maximum number of UTF-16 code units returned by one read.</summary>
	public const int MaximumMaxChars = 200_000;

	/// <summary>Creates a bounded range after validating its offset and requested length.</summary>
	public WebPageDocumentRange(int offset, int maxChars)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(offset);
		ArgumentOutOfRangeException.ThrowIfLessThan(maxChars, 1);
		ArgumentOutOfRangeException.ThrowIfGreaterThan(maxChars, MaximumMaxChars);

		Offset = offset;
		MaxChars = maxChars;
	}

	/// <summary>Zero-based UTF-16 code-unit offset into the current document HTML.</summary>
	public int Offset { get; }

	/// <summary>Maximum UTF-16 code units to return.</summary>
	public int MaxChars { get; }
}

/// <summary>
/// One exact bounded slice of live document HTML together with pagination metadata.
/// </summary>
/// <param name="Html">HTML slice, preserved without trimming or newline normalization.</param>
/// <param name="TotalLength">Full HTML length in JavaScript UTF-16 code units.</param>
/// <param name="NextOffset">Offset for the next read, or null when this is the final slice.</param>
public sealed record WebPageDocumentFragment(
	string Html,
	int TotalLength,
	int? NextOffset)
{
	/// <summary>
	/// Validates a browser-produced fragment against the requested range and derives pagination.
	/// </summary>
	public static WebPageDocumentFragment Create(
		string html,
		int totalLength,
		WebPageDocumentRange range)
	{
		ArgumentNullException.ThrowIfNull(html);
		ArgumentOutOfRangeException.ThrowIfNegative(totalLength);
		if (range.Offset > totalLength)
		{
			throw new ArgumentException(
				"The requested offset is beyond the reported document length.",
				nameof(range));
		}

		var expectedLength = Math.Min(range.MaxChars, totalLength - range.Offset);
		if (html.Length != expectedLength)
		{
			throw new ArgumentException(
				"The returned HTML length is inconsistent with the requested range.",
				nameof(html));
		}

		var endOffset = range.Offset + html.Length;
		return new WebPageDocumentFragment(
			html,
			totalLength,
			endOffset < totalLength ? endOffset : null);
	}
}
