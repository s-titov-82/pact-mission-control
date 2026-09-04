namespace Pact.Presentation.Settings.ViewModels;

/// <summary>
/// Base for one settings-window section: its identity, dirty tracking, and load/save contract.
/// </summary>
public abstract class SettingsSectionViewModelBase : SettingsObservableObject
{

	/// <summary>
	/// Initializes the section's identity.
	/// </summary>
	/// <param name="section">Which section this is.</param>
	/// <param name="label">Navigation label.</param>
	/// <param name="description">Short explanation shown in the section header.</param>
	/// <param name="fileName">Backing settings file name.</param>
	/// <param name="filePath">Absolute path of the backing file.</param>
	protected SettingsSectionViewModelBase(SettingsSection section, string label, string description, string fileName, string filePath)
	{
		Section = section;
		Label = label;
		Description = description;
		FileName = fileName;
		FilePath = filePath;
	}

	/// <summary>Which section this is.</summary>
	public SettingsSection Section { get; }

	/// <summary>Navigation label.</summary>
	public string Label { get; }

	/// <summary>Short explanation shown in the section header.</summary>
	public string Description { get; }

	/// <summary>Backing settings file name.</summary>
	public string FileName { get; }

	/// <summary>Absolute path of the backing file.</summary>
	public string FilePath { get; }

	/// <summary>Whether the section holds unsaved edits.</summary>
	public bool IsDirty { get; protected internal set => SetField(ref field, value); }

	/// <summary>
	/// Message shown beneath the section, typically a validation failure from the last save
	/// attempt. <see langword="null"/> when there is nothing to report.
	/// </summary>
	public string? StatusText { get; protected set => SetField(ref field, value); }

	/// <summary>Loads the section from its backing file, replacing any unsaved edits.</summary>
	public abstract Task LoadAsync(CancellationToken cancellationToken);

	/// <summary>
	/// Validates and saves the section.
	/// </summary>
	/// <returns>
	/// <see langword="true"/> on success. On validation failure this sets
	/// <see cref="StatusText"/> and returns <see langword="false"/> rather than throwing, so the
	/// user keeps their edits and can correct them.
	/// </returns>
	public abstract Task<bool> SaveAsync(CancellationToken cancellationToken);

	/// <summary>Re-reads the section from disk, discarding unsaved edits.</summary>
	public virtual Task ReloadAsync(CancellationToken cancellationToken) => LoadAsync(cancellationToken);

	/// <summary>
	/// Selects an item and optional sub-item by id, for the card deep links. Unknown ids are a
	/// no-op, so a link to a since-deleted item simply opens the section.
	/// </summary>
	public virtual void SelectItem(string? itemId, string? subItemId) { }

	/// <summary>Marks the section as holding unsaved edits.</summary>
	protected void MarkDirty() => IsDirty = true;

	/// <summary>Clears the unsaved-edits flag after a successful save or reload.</summary>
	protected void ClearDirty() => IsDirty = false;
}