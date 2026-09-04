using Pact.App.Avalonia.Lifecycle;

namespace Pact.App.Avalonia.Tests.Fakes;

internal sealed class ImmediateUiTaskDispatcher : IUiTaskDispatcher
{
	public void Post(Action action) => action();

	public Task InvokeAsync(Func<Task> operation) => operation();
}
