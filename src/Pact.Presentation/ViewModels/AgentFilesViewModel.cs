using System.ComponentModel;
using System.Runtime.CompilerServices;
using Pact.Core.Sessions;

namespace Pact.Presentation.ViewModels;

/// <summary>One linked file as the right column lists it.</summary>
/// <param name="Path">Full path, as linked.</param>
/// <param name="FileName">The file's name.</param>
/// <param name="Location">Short place: scratchpad, Temp, project, or the folder's name.</param>
/// <param name="ToolTip">The agent's title, if any, over the full path.</param>
public sealed record AgentFileItem(string Path, string FileName, string Location, string ToolTip);

/// <summary>The files an agent linked in one session, for the right column; follows the session's record.</summary>
public sealed class AgentFilesViewModel : INotifyPropertyChanged, IDisposable
{
	private readonly string? _projectRoot;
	private readonly IReadOnlyList<string> _tempRoots;
	private readonly Func<string, string, CancellationToken, Task> _removeAsync;
	private IReadOnlyList<AgentFileLink> _links;
	private bool _disposed;

	/// <param name="session">The session whose links are listed.</param>
	/// <param name="projectRoot">Root of the session's project, or <see langword="null"/> for ROOT.</param>
	/// <param name="tempRoots">Folders that read as <c>Temp</c>.</param>
	/// <param name="removeAsync">Removes a link: session id, path.</param>
	public AgentFilesViewModel(
		SessionViewModel session,
		string? projectRoot,
		IReadOnlyList<string> tempRoots,
		Func<string, string, CancellationToken, Task> removeAsync)
	{
		Session = session ?? throw new ArgumentNullException(nameof(session));
		_projectRoot = projectRoot;
		_tempRoots = tempRoots ?? throw new ArgumentNullException(nameof(tempRoots));
		_removeAsync = removeAsync ?? throw new ArgumentNullException(nameof(removeAsync));
		_links = session.Record.FileLinks;
		Items = BuildItems();
		session.PropertyChanged += OnSessionPropertyChanged;
	}

	/// <inheritdoc />
	public event PropertyChangedEventHandler? PropertyChanged;

	/// <summary>The temporary folders of this machine: the process's and the user's.</summary>
	public static IReadOnlyList<string> DefaultTempRoots { get; } =
	[
		.. new[]
			{
				Path.GetTempPath(),
				Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp")
			}
			.Where(path => path.Length > 0)
			.Distinct(StringComparer.OrdinalIgnoreCase)
	];

	/// <summary>The session whose links are listed.</summary>
	public SessionViewModel Session { get; }

	/// <summary>Linked files, newest first.</summary>
	public IReadOnlyList<AgentFileItem> Items { get; private set; }

	/// <summary>Whether there is anything to list.</summary>
	public bool HasItems => Items.Count > 0;

	/// <summary>Removes <paramref name="item"/>'s link from the session.</summary>
	public Task RemoveAsync(AgentFileItem item, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(item);
		return _removeAsync(Session.Record.Id, item.Path, cancellationToken);
	}

	/// <inheritdoc />
	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		Session.PropertyChanged -= OnSessionPropertyChanged;
	}

	private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName != nameof(SessionViewModel.Record) || ReferenceEquals(Session.Record.FileLinks, _links))
		{
			return;
		}

		var had = HasItems;
		_links = Session.Record.FileLinks;
		Items = BuildItems();
		OnPropertyChanged(nameof(Items));
		if (had != HasItems)
		{
			OnPropertyChanged(nameof(HasItems));
		}
	}

	private AgentFileItem[] BuildItems() =>
	[
		.. _links.Select(link => new AgentFileItem(
			link.Path,
			Path.GetFileName(link.Path),
			AgentFileLocation.Describe(link.Path, _projectRoot, _tempRoots),
			link.Title is { } title ? title + "\n" + link.Path : link.Path))
	];

	private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
