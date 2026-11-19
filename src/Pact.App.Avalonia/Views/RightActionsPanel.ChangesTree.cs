using Pact.App.Avalonia.Views.Diff;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Views;

internal sealed partial class RightActionsPanel
{
	private IReadOnlyList<ChangedFileViewModel>? _treeFiles;
	private readonly Dictionary<string, ChangedFileTreeNode> _fileNodes = new(StringComparer.Ordinal);
	private Dictionary<string, ChangedFileTreeNode> _folderNodes = new(StringComparer.Ordinal);
	private string? _treeSelectedPath;

	private void ResetChangedFileTree()
	{
		_treeFiles = null;
		_treeSelectedPath = null;
		_folderNodes.Clear();
		_fileNodes.Clear();
		ChangedFilesTree.ItemsSource = null;
	}

	private void RefreshChangedFileTree(ProjectChangesViewModel changes)
	{
		if (!ReferenceEquals(_treeFiles, changes.Files))
		{
			_treeFiles = changes.Files;
			var previousFolders = _folderNodes;
			_folderNodes = new(StringComparer.Ordinal);
			_fileNodes.Clear();
			List<ChangedFileTreeNode> roots = [];
			foreach (var file in changes.Files)
			{
				var parent = GetFolder(file.Folder, roots, previousFolders);
				ChangedFileTreeNode node = new(file.FileName, file.Path, file, parent);
				_fileNodes.Add(file.Path, node);
				(parent?.Children ?? roots).Add(node);
			}
			SortTree(roots);
			ChangedFilesTree.ItemsSource = roots;
		}

		var path = changes.SelectedFile?.Path;
		var selected = path is not null ? _fileNodes.GetValueOrDefault(path) : null;
		if (!string.Equals(path, _treeSelectedPath, StringComparison.Ordinal))
		{
			for (var parent = selected?.Parent; parent is not null; parent = parent.Parent)
			{ parent.IsExpanded = true; }
		}
		_treeSelectedPath = path;
		ChangedFilesTree.SelectedItem = selected;
	}

	private ChangedFileTreeNode? GetFolder(string path, List<ChangedFileTreeNode> roots,
		Dictionary<string, ChangedFileTreeNode> previousFolders)
	{
		if (path.Length == 0)
		{ return null; }
		if (_folderNodes.TryGetValue(path, out var existing))
		{ return existing; }
		var separator = path.LastIndexOf('/');
		var parent = separator < 0 ? null : GetFolder(path[..separator], roots, previousFolders);
		ChangedFileTreeNode folder = new(path[(separator + 1)..], path, file: null, parent)
		{ IsExpanded = previousFolders.GetValueOrDefault(path)?.IsExpanded ?? false };
		_folderNodes.Add(path, folder);
		(parent?.Children ?? roots).Add(folder);
		return folder;
	}

	private static void SortTree(List<ChangedFileTreeNode> nodes)
	{
		nodes.Sort((left, right) => left.IsFolder != right.IsFolder ? (left.IsFolder ? -1 : 1)
			: StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name));
		foreach (var node in nodes)
		{ SortTree(node.Children); }
	}
}
