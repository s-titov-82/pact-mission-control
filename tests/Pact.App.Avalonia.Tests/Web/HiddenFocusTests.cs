using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Pact.App.Avalonia.Web;

namespace Pact.App.Avalonia.Tests.Web;

public sealed class HiddenFocusTests
{
	// Reactivating the window refocuses the last focused element; a hidden WebView2 has no
	// window to take it and the native call throws on the UI thread.
	[AvaloniaTest]
	public void Releasing_a_focused_control_leaves_nothing_to_refocus()
	{
		TextBox page = new();
		Window window = new() { Content = new StackPanel { Children = { page } } };
		window.Show();
		page.Focus();
		page.IsFocused.ShouldBeTrue();

		HiddenFocus.Release(page);

		page.IsFocused.ShouldBeFalse();
		window.FocusManager!.GetFocusedElement().ShouldNotBeSameAs(page);
		window.Close();
	}

	[AvaloniaTest]
	public void Releasing_an_unfocused_control_keeps_the_focus_where_it_is()
	{
		TextBox page = new();
		TextBox other = new();
		Window window = new() { Content = new StackPanel { Children = { page, other } } };
		window.Show();
		other.Focus();

		HiddenFocus.Release(page);

		other.IsFocused.ShouldBeTrue();
		window.Close();
	}
}
