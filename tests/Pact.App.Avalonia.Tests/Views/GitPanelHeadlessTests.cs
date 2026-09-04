using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.LogicalTree;
using Pact.App.Avalonia.Views;

namespace Pact.App.Avalonia.Tests.Views;

public sealed class GitPanelHeadlessTests
{
	[AvaloniaTest]
	public void VisualTreeContainsConflictActionsButNoFixedFetchOrPull()
	{
		GitPanelView view = new();
		var labels = view.GetLogicalDescendants()
			.OfType<Button>()
			.Select(button => button.Content?.ToString() ?? string.Empty)
			.ToArray();

		labels.ShouldNotContain("Fetch");
		labels.ShouldContain("Resolve");
		labels.ShouldContain("Rebase onto base");
		labels.ShouldContain("Abort rebase");
		labels.ShouldNotContain("Pull");
	}
}