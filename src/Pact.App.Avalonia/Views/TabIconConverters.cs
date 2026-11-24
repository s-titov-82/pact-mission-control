using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Svg;

namespace Pact.App.Avalonia.Views;

internal sealed class AgentTabLogoConverter : IMultiValueConverter
{
	private static readonly Lazy<IImage> Codex = new(() => Load("codex"));
	private static readonly Lazy<IImage> Claude = new(() => Load("claude"));
	private static readonly Lazy<IImage> Hermes = new(() => Load("hermes"));

	/// <inheritdoc />
	public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
	{
		if (values[1] is false)
		{
			return null;
		}

		return values[0] switch
		{
			"codex" => Codex.Value,
			"claude" => Claude.Value,
			"hermes" => Hermes.Value,
			_ => null
		};
	}

	private static SvgImage Load(string name) => new()
	{
		Source = SvgSource.Load($"avares://Pact.App.Avalonia/Assets/TabIcons/{name}.svg", baseUri: null)
	};
}

internal sealed class WebTabFaviconConverter : IValueConverter
{
	private static readonly ConditionalWeakTable<byte[], Bitmap> Bitmaps = new();

	/// <inheritdoc />
	public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		if (value is not byte[] { Length: > 0 } png)
		{
			return parameter;
		}

		try
		{
			return Bitmaps.GetValue(png, static bytes =>
			{
				using var stream = new MemoryStream(bytes, writable: false);
				return new Bitmap(stream);
			});
		}
		catch (ArgumentException)
		{
			return parameter;
		}
	}

	/// <inheritdoc />
	public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
		throw new NotSupportedException();
}
