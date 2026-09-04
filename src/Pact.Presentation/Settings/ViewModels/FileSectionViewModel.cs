using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pact.Presentation.Settings.Mapping;

namespace Pact.Presentation.Settings.ViewModels;

/// <summary>
/// Generic helper base for the "tabbed list of items backed by one JSON array file"
/// shape shared by launch profiles, prompt templates, and web link templates. This type
/// is never bound directly from XAML; only its non-generic base and the concrete section
/// subclasses are XAML-visible.
/// </summary>
public abstract class FileSectionViewModel<TItem> : SettingsSectionViewModelBase
	where TItem : SettingsItemViewModelBase
{
	private readonly SettingsFileStore _store;
	private readonly string? _arrayPropertyName;
	private JsonSettingsArray? _array;

	/// <summary>
	/// Initializes the section over one JSON array file.
	/// </summary>
	/// <param name="store">Store used to read and write the file.</param>
	/// <param name="section">Which section this is.</param>
	/// <param name="label">Navigation label.</param>
	/// <param name="description">Short explanation shown in the section header.</param>
	/// <param name="fileName">Backing settings file name.</param>
	/// <param name="arrayPropertyName">
	/// Property holding the array when the document's root is an object, or
	/// <see langword="null"/> when the root is the array itself.
	/// </param>
	protected FileSectionViewModel(
		SettingsFileStore store,
		SettingsSection section,
		string label,
		string description,
		string fileName,
		string? arrayPropertyName = null)
		: base(section, label, description, fileName, ResolvePath(store, fileName))
	{
		ArgumentNullException.ThrowIfNull(store);
		_store = store;
		_arrayPropertyName = arrayPropertyName;
	}

	/// <summary>
	/// Items in file order, including unrecognized entries kept as placeholders so unknown
	/// JSON survives a save.
	/// </summary>
	public ObservableCollection<SettingsItemViewModelBase> Items { get; } = [];

	/// <summary>Item whose tab is shown, or <see langword="null"/> when none is selected.</summary>
	public SettingsItemViewModelBase? SelectedItem
	{
		get;
		set => SetField(ref field, value);
	}

	/// <inheritdoc />
	public override async Task LoadAsync(CancellationToken cancellationToken)
	{
		DetachAllItems();
		Items.Clear();
		SelectedItem = null;
		_array = null;
		StatusText = null;

		// No ConfigureAwait(false): this method mutates the UI-bound Items collection and
		// SelectedItem/StatusText afterwards, so the continuation must stay on the captured
		// (dispatcher) SynchronizationContext.
		var json = await _store.ReadAsync(FileName, cancellationToken);

		JsonSettingsArray array;
		try
		{
			array = JsonSettingsArray.Parse(json, _arrayPropertyName);
		}
		catch (JsonException ex)
		{
			StatusText = $"Failed to load {FileName}: {ex.Message}";
			ClearDirty();
			return;
		}

		_array = array;
		foreach (var node in array.Items)
		{
			var itemVm = (SettingsItemViewModelBase?)TryCreateItem(node) ?? new UnrecognizedItemViewModel(node);
			AttachItem(itemVm);
			Items.Add(itemVm);
		}

		SelectedItem = Items.Count > 0 ? Items[0] : null;
		ClearDirty();
	}

	/// <inheritdoc />
	/// <remarks>
	/// Each item writes only the properties it owns back into its own JSON node, so fields this
	/// build does not recognize are preserved rather than dropped on save.
	/// </remarks>
	public override async Task<bool> SaveAsync(CancellationToken cancellationToken)
	{
		if (_array is null)
		{
			StatusText = "Nothing loaded to save.";
			return false;
		}

		var error = Validate();
		if (error is not null)
		{
			StatusText = error;
			return false;
		}

		foreach (var item in Items)
		{
			item.WriteTo();
		}

		// No ConfigureAwait(false): the loop below mutates each item's (UI-bound) dirty state.
		await _store.SaveAsync(FileName, _array.ToJsonString(), cancellationToken);
		foreach (var item in Items)
		{
			item.ClearItemDirty();
		}

		ClearDirty();
		StatusText = $"Saved {Label} ({Items.Count} items).";
		return true;
	}

	/// <summary>Adds a new backing node and item VM, selects it, and marks the section dirty.</summary>
	public void AddNewItem()
	{
		if (_array is null)
		{
			return;
		}

		var node = _array.AddNew();
		var item = CreateNewItem(node);
		AttachItem(item);
		Items.Add(item);
		SelectedItem = item;
		MarkDirty();
	}

	/// <summary>Removes the item's backing node and drops it from <see cref="Items"/>.</summary>
	public void RemoveItem(SettingsItemViewModelBase item)
	{
		ArgumentNullException.ThrowIfNull(item);

		if (_array is null)
		{
			return;
		}

		var wasSelected = ReferenceEquals(SelectedItem, item);
		if (!Items.Remove(item))
		{
			return;
		}

		DetachItem(item);
		_array.Remove(item.Node);

		if (wasSelected)
		{
			SelectedItem = Items.Count > 0 ? Items[0] : null;
		}

		MarkDirty();
	}

	/// <summary>Maps a loaded node to a strongly-typed item, or null when unrecognized.</summary>
	protected abstract TItem? TryCreateItem(JsonObject node);

	/// <summary>Creates the item VM for a freshly added, empty node.</summary>
	protected abstract TItem CreateNewItem(JsonObject node);

	/// <summary>Returns null when the current items are valid, otherwise a status message.</summary>
	protected abstract string? Validate();

	private void AttachItem(SettingsItemViewModelBase item) => item.Changed += OnItemChanged;

	private void DetachItem(SettingsItemViewModelBase item) => item.Changed -= OnItemChanged;

	private void DetachAllItems()
	{
		foreach (var item in Items)
		{
			DetachItem(item);
		}
	}

	private void OnItemChanged(object? sender, EventArgs e) => MarkDirty();

	private static string ResolvePath(SettingsFileStore store, string fileName)
	{
		ArgumentNullException.ThrowIfNull(store);
		var descriptor = store.Files.First(
			file => string.Equals(file.FileName, fileName, StringComparison.OrdinalIgnoreCase));
		return descriptor.Path;
	}
}
