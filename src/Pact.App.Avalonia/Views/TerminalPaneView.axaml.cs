using Avalonia.Controls;
using Pact.App.Avalonia.Platform;
using Pact.App.Avalonia.Web;

namespace Pact.App.Avalonia.Views;

internal sealed partial class TerminalPaneView : UserControl, IAsyncDisposable
{
	public TerminalPaneView()
	{
		InitializeComponent();
	}

	public TerminalWebViewControl WebViewControl => TerminalControl;
	internal void ConfigureEnvironment(string userDataFolder, string profileName) =>
		TerminalControl.ConfigureEnvironment(userDataFolder, profileName);
	internal void ConfigureProfileHousekeeping(WebViewProfileHousekeeping profileHousekeeping) =>
		TerminalControl.ConfigureProfileHousekeeping(profileHousekeeping);
	public ValueTask DisposeAsync() => TerminalControl.DisposeAsync();
}