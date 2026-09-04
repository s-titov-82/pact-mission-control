using Pact.Core.Presentation;

namespace Pact.Core.Tests.Presentation;

public sealed class WebPageDocumentRangeTests
{
	[Test]
	public void Default_range_uses_the_bounded_default()
	{
		var range = new WebPageDocumentRange(
			0,
			WebPageDocumentRange.DefaultMaxChars);

		range.MaxChars.ShouldBe(100_000);
		WebPageDocumentRange.MaximumMaxChars.ShouldBe(200_000);
	}

	[TestCase(-1, 1)]
	[TestCase(0, 0)]
	[TestCase(0, 200_001)]
	public void Range_rejects_values_outside_the_supported_bounds(
		int offset,
		int maxChars)
	{
		Should.Throw<ArgumentOutOfRangeException>(
			() => new WebPageDocumentRange(offset, maxChars));
	}

	[Test]
	public void Fragment_points_to_the_next_utf16_slice()
	{
		var fragment = WebPageDocumentFragment.Create(
			"5678901234",
			totalLength: 25,
			new WebPageDocumentRange(5, 10));

		fragment.ShouldBe(new WebPageDocumentFragment(
			"5678901234",
			TotalLength: 25,
			NextOffset: 15));
	}

	[Test]
	public void Final_fragment_has_no_next_offset()
	{
		var fragment = WebPageDocumentFragment.Create(
			"56789",
			totalLength: 10,
			new WebPageDocumentRange(5, 10));

		fragment.NextOffset.ShouldBeNull();
	}

	[Test]
	public void Fragment_length_uses_utf16_code_units()
	{
		var fragment = WebPageDocumentFragment.Create(
			"😀",
			totalLength: 4,
			new WebPageDocumentRange(1, 2));

		fragment.NextOffset.ShouldBe(3);
	}

	[TestCase("", 10, 5, 5)]
	[TestCase("short", 20, 5, 10)]
	[TestCase("too long", 5, 0, 10)]
	public void Fragment_rejects_a_slice_inconsistent_with_the_range(
		string html,
		int totalLength,
		int offset,
		int maxChars)
	{
		Should.Throw<ArgumentException>(() =>
			WebPageDocumentFragment.Create(
				html,
				totalLength,
				new WebPageDocumentRange(offset, maxChars)));
	}
}
