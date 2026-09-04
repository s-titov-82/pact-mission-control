using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;

namespace Pact.Presentation.Settings.ViewModels;

/// <summary>
/// Base for a single item (tab) inside a <see cref="FileSectionViewModel{TItem}"/>.
/// Concrete items keep a reference to their own <see cref="JsonObject"/> node and only
/// write the properties they own back into it, so unknown fields survive round-tripping.
/// </summary>
public abstract class SettingsItemViewModelBase : SettingsObservableObject
{

	/// <summary>Display text for the item's tab: display name (or fallback) plus " •" when dirty.</summary>
	public abstract string TabHeader { get; }

	/// <summary>The JSON node this item edits in place.</summary>
	public abstract JsonObject Node { get; }

	/// <summary>False for node-preserving placeholders that must be edited through raw JSON.</summary>
	public virtual bool IsRecognized => true;

	/// <summary>Whether this item holds unsaved edits; drives the bullet in its tab header.</summary>
	public bool IsItemDirty
	{
		get;
		private set
		{
			if (SetField(ref field, value))
			{
				OnPropertyChanged(nameof(TabHeader));
			}
		}
	}

	/// <summary>Raised whenever an editable field changes; the owning section marks itself dirty.</summary>
	public event EventHandler? Changed;

	/// <summary>Writes this item's fields into its <see cref="Node"/>. No-op for read-only items.</summary>
	internal virtual void WriteTo()
	{
	}

	/// <summary>Call from a field setter after <c>SetField</c> reports a real change.</summary>
	[SuppressMessage(
		"Design",
		"CA1030:Use events where appropriate",
		Justification = "This helper marks the item dirty before raising the existing Changed event; it is not a second event contract.")]
	protected void RaiseChanged()
	{
		IsItemDirty = true;
		Changed?.Invoke(this, EventArgs.Empty);
	}

	/// <summary>Resets the dirty flag once the owning section has been saved successfully.</summary>
	internal void ClearItemDirty() => IsItemDirty = false;
}