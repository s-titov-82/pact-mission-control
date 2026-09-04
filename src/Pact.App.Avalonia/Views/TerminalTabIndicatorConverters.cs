using System.Globalization;
using Avalonia.Data.Converters;
using Pact.Core.Sessions;

namespace Pact.App.Avalonia.Views;

internal sealed class TerminalTabIndicatorGlyphConverter : IValueConverter
{
	public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
		value switch
		{
			TerminalTabIndicator.Busy => "⠋",
			TerminalTabIndicator.InputRequested => "?",
			TerminalTabIndicator.Unread => "●",
			TerminalTabIndicator.Paused => string.Empty,
			TerminalTabIndicator.Failed => "●",
			_ => string.Empty
		};

	public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
		throw new NotSupportedException();
}

internal sealed class TerminalTabIndicatorVisibleConverter : IValueConverter
{
	public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
		value is TerminalTabIndicator indicator && indicator != TerminalTabIndicator.None;

	public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
		throw new NotSupportedException();
}

internal sealed class TerminalTabIndicatorEqualsConverter : IValueConverter
{
	public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
		value is TerminalTabIndicator indicator
		&& parameter is string expected
		&& Enum.TryParse(expected, ignoreCase: true, out TerminalTabIndicator parsed)
		&& indicator == parsed;

	public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
		throw new NotSupportedException();
}
