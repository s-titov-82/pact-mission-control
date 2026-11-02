using Avalonia.Controls;
using Avalonia.Interactivity;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Views;

/// <summary>A relocatable view of current tab attention; the shell owns navigation.</summary>
internal sealed partial class AttentionFeedView : UserControl
{
	/// <summary>Creates the attention cards.</summary>
	public AttentionFeedView()
	{
		InitializeComponent();
		AddHandler(Button.ClickEvent, OnItemClicked);
	}

	/// <summary>Requests ordinary activation of a still-current attention target.</summary>
	public event EventHandler<AttentionItem>? ItemRequested;

	private void OnItemClicked(object? sender, RoutedEventArgs args)
	{
		if (args.Source is Button { DataContext: AttentionItem item }
			&& DataContext is AttentionFeedViewModel feed && feed.Contains(item))
		{
			args.Handled = true;
			ItemRequested?.Invoke(this, item);
		}
	}
}
