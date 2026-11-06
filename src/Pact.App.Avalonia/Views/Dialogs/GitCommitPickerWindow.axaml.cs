using Avalonia.Controls;
using Avalonia.Interactivity;
using Pact.App.Avalonia.Lifecycle;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Views.Dialogs;

internal sealed partial class GitCommitPickerWindow : Window, IDisposable
{
	private readonly GitCommitPickerViewModel _model;
	private readonly ObservedTaskGroup _tasks;
	private readonly CancellationTokenSource _lifetime = new();
	private bool _disposed;
	public GitCommitPickerWindow() : this(new GitCommitPickerViewModel(Environment.CurrentDirectory, new GitWorkingTreeReader(new GitCliRunner())),
		new ObservedTaskGroup(static (_, _) => Task.CompletedTask))
	{ }
	public GitCommitPickerWindow(GitCommitPickerViewModel model, ObservedTaskGroup tasks)
	{
		InitializeComponent();
		_model = model;
		_tasks = tasks;
		DataContext = model;
		Opened += (_, _) => LoadMore();
		Closed += (_, _) => Dispose();
	}
	public void Dispose()
	{
		if (_disposed)
		{ return; }
		_disposed = true;
		_lifetime.Cancel();
		_model.Dispose();
		_lifetime.Dispose();
	}
	private void OnLoadMoreClicked(object? sender, RoutedEventArgs e) => LoadMore();
	private void LoadMore() => _tasks.TryRun("commit-picker-history", () => _model.LoadMoreAsync(_lifetime.Token), static _ => Task.CompletedTask);
	private void OnAcceptClicked(object? sender, RoutedEventArgs e) => _tasks.TryRun("commit-picker-open", async () =>
	{
		if (await _model.AcceptAsync(_lifetime.Token) is { } commit && !_lifetime.IsCancellationRequested)
		{ Close(commit); }
	}, static _ => Task.CompletedTask);
	private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close();
}