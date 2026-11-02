using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pact.App.Avalonia.Views;
using Pact.Core.RootTabs;
using Pact.Core.Web;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Tests.Views;

public sealed class AttentionFeedViewHeadlessTests
{
	[AvaloniaTest]
	public void Cards_follow_current_attention_and_a_removed_card_cannot_request_navigation()
	{
		RootTabsViewModel root = new(RootTabsRecord.CreateDefault());
		using AttentionFeedViewModel feed = new([], root, new());
		AttentionFeedView view = new() { DataContext = feed };
		FluentTheme theme = new();
		Application.Current!.Styles.Add(theme);
		Window window = new() { Content = view, Width = 300, Height = 400 };
		window.Show();
		try
		{
			view.IsVisible.ShouldBeFalse();
			WebPageViewModel page = new(new WebPageRecord("web", "Build results", "https://example.com", "https://example.com", DateTimeOffset.Now, DateTimeOffset.Now));
			root.WebPages.Add(page);
			page.SetMonitorUnread(true);
			Dispatcher.UIThread.RunJobs();
			window.UpdateLayout();
			view.IsVisible.ShouldBeTrue();
			var button = view.GetVisualDescendants().OfType<Button>().ShouldHaveSingleItem();
			button.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ShouldContain("ROOT");
			button.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ShouldContain("Build results");
			List<AttentionItem> requested = [];
			view.ItemRequested += (_, item) => requested.Add(item);
			button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			requested.ShouldHaveSingleItem().Target.ShouldBeSameAs(page);
			page.HasMonitorUnread.ShouldBeTrue();
			page.SetMonitorUnread(false);
			Dispatcher.UIThread.RunJobs();
			view.IsVisible.ShouldBeFalse();
			button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			requested.Count.ShouldBe(1);
		}
		finally
		{
			window.Close();
			Application.Current.Styles.Remove(theme);
		}
	}

	[AvaloniaTest]
	public void Many_cards_scroll_inside_a_bounded_region()
	{
		RootTabsViewModel root = new(RootTabsRecord.CreateDefault());
		for (var index = 0; index < 30; index++)
		{
			WebPageViewModel page = new(new WebPageRecord($"web-{index}", $"Page {index}", "https://example.com", "https://example.com", DateTimeOffset.Now, DateTimeOffset.Now));
			page.SetMonitorUnread(true);
			root.WebPages.Add(page);
		}
		using AttentionFeedViewModel feed = new([], root, new());
		AttentionFeedView view = new() { DataContext = feed };
		FluentTheme theme = new();
		Application.Current!.Styles.Add(theme);
		Window window = new() { Content = view, Width = 300, Height = 400 };
		window.Show();
		try
		{
			window.UpdateLayout();
			var scroller = view.GetVisualDescendants().OfType<ScrollViewer>().ShouldHaveSingleItem();
			scroller.Viewport.Height.ShouldBeLessThanOrEqualTo(180);
			scroller.Extent.Height.ShouldBeGreaterThan(scroller.Viewport.Height);
			scroller.Offset = new Vector(0, scroller.Extent.Height);
			window.UpdateLayout();
			scroller.Offset.Y.ShouldBeGreaterThan(0);
		}
		finally
		{
			window.Close();
			Application.Current.Styles.Remove(theme);
		}
	}
}
