using System.Threading.Channels;

namespace Pact.Infrastructure.AgentControl;

/// <summary>
/// Owns bounded in-memory notification queues for authenticated MCP notification streams.
/// </summary>
public sealed class AgentControlNotificationHub
{
	/// <summary>Exact JSON-RPC payload emitted when the ordinary tool catalog changes.</summary>
	public const string ToolsListChangedJson =
		"""{"jsonrpc":"2.0","method":"notifications/tools/list_changed"}""";

	private readonly Lock _sync = new();
	private readonly Dictionary<Guid, SubscriptionState> _subscriptions = [];
	private bool _completed;

	/// <summary>Creates one disposable stream subscription for an authenticated caller.</summary>
	public AgentControlNotificationSubscription Subscribe(AgentControlCaller caller)
	{
		ArgumentNullException.ThrowIfNull(caller);
		var id = Guid.NewGuid();
		var channel = Channel.CreateBounded<string>(
			new BoundedChannelOptions(1)
			{
				FullMode = BoundedChannelFullMode.DropOldest,
				SingleReader = true,
				SingleWriter = false
			});

		lock (_sync)
		{
			if (_completed)
			{
				channel.Writer.TryComplete();
			}
			else
			{
				_subscriptions.Add(id, new SubscriptionState(caller, channel));
			}
		}

		return new AgentControlNotificationSubscription(
			channel.Reader,
			() => Remove(id));
	}

	/// <summary>Coalesces one tool-list change into every ordinary caller's pending queue.</summary>
	public void PublishToolsListChanged()
	{
		lock (_sync)
		{
			foreach (SubscriptionState subscription in _subscriptions.Values)
			{
				if (!subscription.Caller.IsOrchestrator)
				{
					subscription.Channel.Writer.TryWrite(ToolsListChangedJson);
				}
			}
		}
	}

	/// <summary>Ends every current and future reader without persisting pending notifications.</summary>
	public void Complete()
	{
		SubscriptionState[] subscriptions;
		lock (_sync)
		{
			if (_completed)
			{
				return;
			}

			_completed = true;
			subscriptions = [.. _subscriptions.Values];
			_subscriptions.Clear();
		}

		foreach (SubscriptionState subscription in subscriptions)
		{
			subscription.Channel.Writer.TryComplete();
		}
	}

	private void Remove(Guid id)
	{
		SubscriptionState? subscription;
		lock (_sync)
		{
			if (!_subscriptions.Remove(id, out subscription))
			{
				return;
			}
		}

		subscription.Channel.Writer.TryComplete();
	}

	private sealed record SubscriptionState(
		AgentControlCaller Caller,
		Channel<string> Channel);
}

/// <summary>Exposes one bounded notification reader and removes it when disposed.</summary>
public sealed class AgentControlNotificationSubscription : IDisposable, IAsyncDisposable
{
	private readonly Action _dispose;
	private int _disposed;

	internal AgentControlNotificationSubscription(
		ChannelReader<string> reader,
		Action dispose)
	{
		Reader = reader;
		_dispose = dispose;
	}

	/// <summary>Gets the single-reader stream of JSON-RPC notification payloads.</summary>
	public ChannelReader<string> Reader { get; }

	/// <summary>Removes this reader from the hub.</summary>
	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) == 0)
		{
			_dispose();
		}
	}

	/// <summary>Removes this reader from the hub without asynchronous cleanup work.</summary>
	public ValueTask DisposeAsync()
	{
		Dispose();
		return ValueTask.CompletedTask;
	}
}
