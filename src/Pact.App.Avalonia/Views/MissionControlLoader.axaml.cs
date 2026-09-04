using Avalonia.Controls;
using Avalonia.Labs.Gif;
using Avalonia.Styling;

namespace Pact.App.Avalonia.Views;

/// <summary>Displays the shared PACT:> busy indicator while keeping its prompt stationary.</summary>
internal sealed partial class MissionControlLoader : UserControl
{
	public MissionControlLoader()
	{
		InitializeComponent();
		ActualThemeVariantChanged += (_, _) => UpdateAnimationSource();
		UpdateAnimationSource();
	}

	private void UpdateAnimationSource()
	{
		var gifVariant = ActualThemeVariant == ThemeVariant.Dark ? "darktheme" : "lighttheme";

		Animation.Source = GifStreamSource.FromUriString(
			$"avares://Pact.App.Avalonia/Assets/MissionControlLoader_transparent_{gifVariant}.gif");
	}
}