using System.Globalization;
using Avalonia.Data.Converters;

namespace Pact.App.Avalonia.Views.Settings;

internal sealed class NullToBooleanConverter : IValueConverter
{
	public bool Invert { get; set; }

	public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		var hasValue = value is not null;
		return Invert ? !hasValue : hasValue;
	}

	public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
		throw new NotSupportedException();
}

internal sealed class BooleanNegationConverter : IValueConverter
{
	public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
		value is not true;

	public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
		throw new NotSupportedException();
}