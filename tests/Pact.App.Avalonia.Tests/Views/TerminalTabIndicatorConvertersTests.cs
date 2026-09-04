using System.Globalization;
using Pact.App.Avalonia.Views;
using Pact.Core.Sessions;

namespace Pact.App.Avalonia.Tests.Views;

[TestFixture]
public sealed class TerminalTabIndicatorConvertersTests
{
	[Test]
	public void Input_requested_has_a_visible_question_glyph()
	{
		TerminalTabIndicatorGlyphConverter glyphConverter = new();
		TerminalTabIndicatorVisibleConverter visibleConverter = new();

		var glyph = glyphConverter.Convert(
			TerminalTabIndicator.InputRequested,
			typeof(string),
			parameter: null,
			CultureInfo.InvariantCulture);
		var visible = visibleConverter.Convert(
			TerminalTabIndicator.InputRequested,
			typeof(bool),
			parameter: null,
			CultureInfo.InvariantCulture);

		glyph.ShouldBe("?");
		visible.ShouldBe(true);
	}
}
