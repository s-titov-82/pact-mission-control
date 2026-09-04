using Avalonia.Threading;

namespace Pact.App.Avalonia.Lifecycle;

/// <summary>
/// Provides the application's single boundary for ordered UI dispatch and asynchronous UI work.
/// </summary>
internal interface IUiTaskDispatcher
{
	void Post(Action action);

	Task InvokeAsync(Func<Task> operation);
}

/// <summary>
/// Marshals work through Avalonia's UI dispatcher while preserving synchronous callback order.
/// </summary>
internal sealed class UiTaskDispatcher : IUiTaskDispatcher
{
	public void Post(Action action)
	{
		ArgumentNullException.ThrowIfNull(action);
		if (Dispatcher.UIThread.CheckAccess())
		{
			action();
			return;
		}

		Dispatcher.UIThread.InvokeAsync(action).GetAwaiter().GetResult();
	}

	public async Task InvokeAsync(Func<Task> operation)
	{
		ArgumentNullException.ThrowIfNull(operation);
		if (Dispatcher.UIThread.CheckAccess())
		{
			await operation();
			return;
		}

		await Dispatcher.UIThread.InvokeAsync(operation);
	}
}