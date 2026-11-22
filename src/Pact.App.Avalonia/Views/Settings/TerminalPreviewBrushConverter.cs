using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Styling;

namespace Pact.App.Avalonia.Views.Settings;

internal sealed class TerminalPreviewBrushConverter : IMultiValueConverter
{
	public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
	{
		if (values[0] is string color)
		{
			return Brush.Parse(color);
		}
		var dark = values[1] is AppearanceMode.Dark || (values[1] is AppearanceMode.System && Equals(values[2], ThemeVariant.Dark));
		return Brush.Parse(parameter switch
		{
			"background" => dark ? "#09090b" : "#F8FAFC",
			"green" => dark ? "#4ade80" : "#15803D",
			_ => dark ? "#e5e7eb" : "#111827"
		});
	}
}
