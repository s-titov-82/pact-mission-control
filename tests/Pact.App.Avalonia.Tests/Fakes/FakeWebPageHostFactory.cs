using Pact.Core.Presentation;
using Pact.Core.Web.Monitoring;

namespace Pact.App.Avalonia.Tests.Fakes;

internal sealed class FakeWebPageHostFactory : IWebPageHostFactory
{
	public Dictionary<string, FakeWebPageHost> Hosts { get; } = new(StringComparer.Ordinal);
	public Action<FakeWebPageHost>? ConfigureHost { get; set; }

	public Task<IWebPageHost> CreateAsync(string id, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		FakeWebPageHost host = new(id);
		ConfigureHost?.Invoke(host);
		Hosts.Add(id, host);
		return Task.FromResult<IWebPageHost>(host);
	}
}

internal sealed class FakeWebPageHost(string id) : IWebPageHost
{
	private readonly TaskCompletionSource<object?> _disposed =
		new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly TaskCompletionSource<object?> _nonNullQuery =
		new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly List<(int MinimumCount, TaskCompletionSource<object?> Completion)> _evaluationWaiters = [];
	private readonly Lock _signalSync = new();
	private EventHandler<Uri>? _sourceChangedHandlers;
	private EventHandler<string>? _titleChangedHandlers;
	private EventHandler? _navigationStartedHandlers;
	private EventHandler? _navigationCompletedHandlers;
	private event EventHandler<string>? NavigationFailedHandlers;
	public string Id { get; } = id;
	public Uri? Source
	{
		get
		{
			Calls.Add("read-source");
			return field;
		}
		private set;
	}
	public List<string> Calls { get; } = [];
	public Queue<Task<WebMonitorEvaluation>> EvaluationResults { get; } = [];
	public string DocumentHtml { get; set; } = string.Empty;
	public List<WebMonitorDomQuery?> EvaluationQueries { get; } = [];
	public List<CancellationToken> EvaluationCancellationTokens { get; } = [];
	public Action? Disposing { get; set; }
	public bool RaiseNavigationEventsOnNavigate { get; set; }
	public event EventHandler<Uri>? SourceChanged
	{
		add
		{
			Calls.Add("subscribe-source");
			_sourceChangedHandlers += value;
		}
		remove => _sourceChangedHandlers -= value;
	}
	public event EventHandler<string>? TitleChanged
	{
		add
		{
			Calls.Add("subscribe-title");
			_titleChangedHandlers += value;
		}
		remove => _titleChangedHandlers -= value;
	}
	public event EventHandler? NavigationStarted
	{
		add
		{
			Calls.Add("subscribe-navigation-started");
			_navigationStartedHandlers += value;
		}
		remove => _navigationStartedHandlers -= value;
	}
	public event EventHandler? NavigationCompleted
	{
		add
		{
			Calls.Add("subscribe-navigation-completed");
			_navigationCompletedHandlers += value;
		}
		remove => _navigationCompletedHandlers -= value;
	}
	public event EventHandler<string>? NavigationFailed
	{
		add => NavigationFailedHandlers += value;
		remove => NavigationFailedHandlers -= value;
	}
	public event EventHandler<Uri>? NewWindowRequested { add { } remove { } }

	public Task NavigateAsync(Uri uri, CancellationToken cancellationToken)
	{
		if (RaiseNavigationEventsOnNavigate)
		{
			RaiseNavigationStarted();
		}

		Source = uri;
		Calls.Add("navigate");
		if (RaiseNavigationEventsOnNavigate)
		{
			RaiseSourceChanged(uri);
			RaiseNavigationCompleted();
		}

		return Task.CompletedTask;
	}

	public Task ReloadAsync(CancellationToken cancellationToken)
	{
		Calls.Add("reload");
		return Task.CompletedTask;
	}

	public Task ShowAsync(CancellationToken cancellationToken)
	{
		Calls.Add("show");
		return Task.CompletedTask;
	}

	public Task HideAsync(CancellationToken cancellationToken)
	{
		Calls.Add("hide");
		return Task.CompletedTask;
	}

	public Task FocusAsync(CancellationToken cancellationToken)
	{
		Calls.Add("focus");
		return Task.CompletedTask;
	}

	public Task<WebMonitorEvaluation> EvaluateMonitorAsync(
		WebMonitorDomQuery? query,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		Calls.Add("evaluate");
		List<TaskCompletionSource<object?>> completedWaiters = [];
		lock (_signalSync)
		{
			EvaluationQueries.Add(query);
			if (query is not null)
			{
				_nonNullQuery.TrySetResult(null);
			}
			for (var index = _evaluationWaiters.Count - 1; index >= 0; index--)
			{
				var waiter = _evaluationWaiters[index];
				if (waiter.MinimumCount <= EvaluationQueries.Count)
				{
					completedWaiters.Add(waiter.Completion);
					_evaluationWaiters.RemoveAt(index);
				}
			}
		}

		foreach (var waiter in completedWaiters)
		{
			waiter.TrySetResult(null);
		}

		EvaluationCancellationTokens.Add(cancellationToken);
		if (EvaluationResults.Count == 0)
		{
			throw new InvalidOperationException("No monitor evaluation result was queued.");
		}

		return EvaluationResults.Dequeue();
	}

	public Task<WebPageDocumentFragment> ReadDocumentHtmlAsync(
		WebPageDocumentRange range,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var length = Math.Min(
			range.MaxChars,
			Math.Max(0, DocumentHtml.Length - range.Offset));
		var html = range.Offset <= DocumentHtml.Length
			? DocumentHtml.Substring(range.Offset, length)
			: string.Empty;
		return Task.FromResult(WebPageDocumentFragment.Create(
			html,
			DocumentHtml.Length,
			range));
	}

	public Task WaitForEvaluationAsync(int minimumCount)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumCount);
		lock (_signalSync)
		{
			if (EvaluationQueries.Count >= minimumCount)
			{
				return Task.CompletedTask;
			}

			TaskCompletionSource<object?> completion =
				new(TaskCreationOptions.RunContinuationsAsynchronously);
			_evaluationWaiters.Add((minimumCount, completion));
			return completion.Task;
		}
	}

	public Task WaitForNonNullQueryAsync() => _nonNullQuery.Task;

	public Task WaitForDisposalAsync() => _disposed.Task;

	public ValueTask DisposeAsync()
	{
		Disposing?.Invoke();
		Calls.Add("dispose");
		_disposed.TrySetResult(null);
		return ValueTask.CompletedTask;
	}

	public void RaiseSourceChanged(Uri uri)
	{
		Source = uri;
		_sourceChangedHandlers?.Invoke(this, uri);
	}

	public void RaiseTitleChanged(string title) =>
		_titleChangedHandlers?.Invoke(this, title);

	public void RaiseNavigationStarted() =>
		_navigationStartedHandlers?.Invoke(this, EventArgs.Empty);

	public void RaiseNavigationCompleted() =>
		_navigationCompletedHandlers?.Invoke(this, EventArgs.Empty);

	public void RaiseNavigationFailed(string message) =>
		NavigationFailedHandlers?.Invoke(this, message);
}
