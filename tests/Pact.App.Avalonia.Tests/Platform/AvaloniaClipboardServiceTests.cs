using Pact.App.Avalonia.Lifecycle;
using Pact.App.Avalonia.Platform;

namespace Pact.App.Avalonia.Tests.Platform;

public sealed class AvaloniaClipboardServiceTests
{
	[Test]
	public async Task Clipboard_write_is_marshaled_through_the_ui_dispatcher()
	{
		DispatchGuard dispatcher = new();
		AvaloniaClipboardService service = new(
			dispatcher,
			readTextAsync: () => Task.FromResult(string.Empty),
			setTextAsync: _ => dispatcher.RequireDispatch());

		var written = await Task.Run(() => service.TrySetTextAsync("copied text"));

		written.ShouldBeTrue();
	}

	[Test]
	public async Task Clipboard_read_is_marshaled_through_the_ui_dispatcher()
	{
		DispatchGuard dispatcher = new();
		AvaloniaClipboardService service = new(
			dispatcher,
			readTextAsync: () => dispatcher.RequireDispatch("pasted text"),
			setTextAsync: _ => Task.CompletedTask);

		var text = await Task.Run(service.GetTextAsync);

		text.ShouldBe("pasted text");
	}

	private sealed class DispatchGuard : IUiTaskDispatcher
	{
		private readonly AsyncLocal<bool> _insideDispatch = new();

		public bool IsInsideDispatch => _insideDispatch.Value;

		public Task RequireDispatch()
		{
			RequireDispatch<object?>(null);
			return Task.CompletedTask;
		}

		public Task<T> RequireDispatch<T>(T result)
		{
			if (!IsInsideDispatch)
			{
				throw new InvalidOperationException("Clipboard access was not dispatched.");
			}

			return Task.FromResult(result);
		}

		public void Post(Action action) => action();

		public async Task InvokeAsync(Func<Task> operation)
		{
			_insideDispatch.Value = true;
			try
			{
				await operation();
			}
			finally
			{
				_insideDispatch.Value = false;
			}
		}
	}
}
