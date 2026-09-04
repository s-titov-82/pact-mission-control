namespace Pact.App.Avalonia.Web;

internal sealed class WebViewInitializationGate
{
	private readonly Lock _sync = new();
	private readonly Uri _source;
	private readonly TaskCompletionSource<bool> _completion =
		new(TaskCreationOptions.RunContinuationsAsynchronously);
	private bool _navigationCompleted;
	private bool _javaScriptReady;

	public WebViewInitializationGate(Uri source)
	{
		ArgumentNullException.ThrowIfNull(source);
		_source = source;
	}

	public Task Completion => _completion.Task;

	public string[] MissingSignals
	{
		get
		{
			lock (_sync)
			{
				List<string> missing = [];
				if (!_navigationCompleted)
				{
					missing.Add("navigation-completed");
				}

				if (!_javaScriptReady)
				{
					missing.Add("javascript-ready");
				}

				return missing.ToArray();
			}
		}
	}

	public void ReportNavigationCompleted(bool isSuccess)
	{
		lock (_sync)
		{
			if (_completion.Task.IsCompleted || _navigationCompleted)
			{
				return;
			}

			_navigationCompleted = true;
			if (!isSuccess)
			{
				_completion.TrySetException(new InvalidOperationException(
					$"Terminal WebView navigation failed for '{_source.AbsoluteUri}' (IsSuccess=False)."));
				return;
			}
			TryComplete();
		}
	}

	public void ReportJavaScriptReady()
	{
		lock (_sync)
		{
			if (_completion.Task.IsCompleted || _javaScriptReady)
			{
				return;
			}

			_javaScriptReady = true;
			TryComplete();
		}
	}

	public void Cancel(Exception reason)
	{
		ArgumentNullException.ThrowIfNull(reason);
		lock (_sync)
		{
			_completion.TrySetException(reason);
		}
	}

	private void TryComplete()
	{
		if (_navigationCompleted && _javaScriptReady)
		{
			_completion.TrySetResult(true);
		}
	}
}