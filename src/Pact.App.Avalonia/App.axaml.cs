using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Microsoft.Extensions.DependencyInjection;
using Pact.App.Avalonia.Views;

namespace Pact.App.Avalonia;

internal sealed partial class App : Application
{
	internal static AppBootstrap Bootstrap { get; set; } = null!;
	internal static AppearancePreferences CurrentAppearance { get; private set; } =
		new(AppearanceMode.System);
	public static IServiceProvider Services => Bootstrap.Services;

	public override void Initialize() => AvaloniaXamlLoader.Load(this);

	public override void OnFrameworkInitializationCompleted()
	{
		var appearance = Services.GetRequiredService<AppearanceSettingsStore>()
			.LoadPreferencesAsync(CancellationToken.None).GetAwaiter().GetResult();
		ApplyAppearance(appearance);

		if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
		{
			desktop.MainWindow = new MainWindow();
		}

		base.OnFrameworkInitializationCompleted();
	}

	internal static ThemeVariant ToThemeVariant(AppearanceMode mode) => mode switch
	{
		AppearanceMode.Light => ThemeVariant.Light,
		AppearanceMode.Dark => ThemeVariant.Dark,
		_ => ThemeVariant.Default
	};

	internal static void ApplyAppearance(AppearanceMode mode)
	{
		if (Current is { } application)
		{
			application.RequestedThemeVariant = ToThemeVariant(mode);
		}
	}

	internal static void ApplyAppearance(AppearancePreferences preferences)
	{
		ArgumentNullException.ThrowIfNull(preferences);
		CurrentAppearance = preferences;
		ApplyAppearance(preferences.Theme);
	}

	internal static void Shutdown(int exitCode)
	{
		if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
		{
			desktop.Shutdown(exitCode);
		}
	}
}
