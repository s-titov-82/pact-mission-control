using Avalonia.Controls;
using Avalonia.Interactivity;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Views.Remarks;

/// <summary>Carries an action a remark view asks the screen to run.</summary>
internal sealed class RemarkActionEventArgs(RoutedEvent routedEvent, string name, Func<CancellationToken, Task> action)
	: RoutedEventArgs(routedEvent)
{
	/// <summary>The action's name, for the failure log.</summary>
	public string Name { get; } = name;

	/// <summary>The action.</summary>
	public Func<CancellationToken, Task> Action { get; } = action;
}

/// <summary>One remark thread: its messages, the draft box and the actions on it.</summary>
internal sealed partial class RemarkThreadView : UserControl
{
	/// <summary>Bubbles an action up to the screen, which runs it with the app's other UI work.</summary>
	public static readonly RoutedEvent<RemarkActionEventArgs> ActionRequestedEvent =
		RoutedEvent.Register<RemarkThreadView, RemarkActionEventArgs>(nameof(ActionRequestedEvent), RoutingStrategies.Bubble);

	public RemarkThreadView()
	{
		InitializeComponent();
	}

	private RemarkThreadViewModel? Thread => DataContext as RemarkThreadViewModel;

	private void OnSaveClicked(object? sender, RoutedEventArgs e) => Request("remark-save", thread => thread.SaveDraftAsync);

	private void OnDiscardClicked(object? sender, RoutedEventArgs e) => Request("remark-discard", thread => thread.DiscardDraftAsync);

	private void OnResolveClicked(object? sender, RoutedEventArgs e) => Request("remark-resolve", thread => thread.ResolveAsync);

	private void OnReopenClicked(object? sender, RoutedEventArgs e) => Request("remark-reopen", thread => thread.ReopenAsync);

	private void OnRetryClicked(object? sender, RoutedEventArgs e) => Request("remark-retry", thread => thread.RetryAsync);

	private void OnMarkSentClicked(object? sender, RoutedEventArgs e) => Request("remark-mark-sent", thread => thread.MarkSentAsync);

	private void Request(string name, Func<RemarkThreadViewModel, Func<CancellationToken, Task>> action)
	{
		if (Thread is { } thread)
		{
			RaiseEvent(new RemarkActionEventArgs(ActionRequestedEvent, name, action(thread)));
		}
	}
}
