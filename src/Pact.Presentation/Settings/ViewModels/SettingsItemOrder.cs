using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Pact.Presentation.Settings.ViewModels;

/// <summary>Commands for moving the selected tab one place within its displayed list.</summary>
public sealed class SettingsItemOrder<T> where T : SettingsItemViewModelBase
{
	internal SettingsItemOrder(ObservableCollection<T> items, INotifyPropertyChanged owner,
		Func<T?> selected, Action<T, T> swap, Action<T> select)
	{
		MoveLeft = Command(-1);
		MoveRight = Command(1);
		items.CollectionChanged += (_, _) => Refresh();
		owner.PropertyChanged += (_, args) =>
		{
			if (args.PropertyName == "SelectedItem")
			{
				Refresh();
			}
		};

		RelayCommand Command(int delta)
		{
			return new(() =>
			{
				if (selected() is { } item && CanMove(delta))
				{
					swap(item, items[items.IndexOf(item) + delta]);
					select(item);
				}
			}, () => CanMove(delta));
		}

		bool CanMove(int delta)
		{
			var index = selected() is { } item ? items.IndexOf(item) : -1;
			return index >= 0 && index + delta >= 0 && index + delta < items.Count;
		}
	}

	/// <summary>Moves the selection toward the start of the list.</summary>
	public IRelayCommand MoveLeft { get; }

	/// <summary>Moves the selection toward the end of the list.</summary>
	public IRelayCommand MoveRight { get; }

	private void Refresh()
	{
		MoveLeft.NotifyCanExecuteChanged();
		MoveRight.NotifyCanExecuteChanged();
	}
}
