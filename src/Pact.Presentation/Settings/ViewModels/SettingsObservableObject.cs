using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Pact.Presentation.Settings.ViewModels;

/// <summary>
/// Minimal change-notification base for the settings view models, kept local so the settings
/// tree does not depend on an MVVM framework's base type.
/// </summary>
public abstract class SettingsObservableObject : INotifyPropertyChanged
{
	/// <inheritdoc />
	public event PropertyChangedEventHandler? PropertyChanged;

	/// <summary>
	/// Raises <see cref="PropertyChanged"/> for the calling member unless a name is given.
	/// </summary>
	protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
		=> PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

	/// <summary>
	/// Assigns <paramref name="value"/> and notifies only when it differs from the current value.
	/// </summary>
	/// <returns>
	/// <see langword="true"/> when the value actually changed, which callers use to avoid
	/// marking an item dirty on a no-op assignment.
	/// </returns>
	protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
	{
		if (EqualityComparer<T>.Default.Equals(field, value))
		{
			return false;
		}

		field = value;
		OnPropertyChanged(propertyName);
		return true;
	}
}