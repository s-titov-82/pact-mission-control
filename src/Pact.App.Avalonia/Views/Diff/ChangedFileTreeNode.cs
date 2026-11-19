using System.ComponentModel;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Views.Diff;

internal sealed class ChangedFileTreeNode(string name, string path, ChangedFileViewModel? file, ChangedFileTreeNode? parent) : INotifyPropertyChanged
{
	public event PropertyChangedEventHandler? PropertyChanged;
	public string Name { get; } = name;
	public string Path { get; } = path;
	public ChangedFileViewModel? File { get; } = file;
	public bool IsFolder => File is null;
	public List<ChangedFileTreeNode> Children { get; } = [];
	internal ChangedFileTreeNode? Parent { get; } = parent;
	public bool IsExpanded
	{
		get;
		set
		{
			if (field == value)
			{ return; }
			field = value;
			PropertyChanged?.Invoke(this, new(nameof(IsExpanded)));
		}
	}
}
