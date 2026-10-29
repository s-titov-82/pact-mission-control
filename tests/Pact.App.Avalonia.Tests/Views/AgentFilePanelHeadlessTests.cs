using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Markdown.Avalonia.Full;
using Pact.App.Avalonia.Views;
using Pact.App.Avalonia.Views.Editing;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Tests.Views;

public sealed class AgentFilePanelHeadlessTests
{
	[AvaloniaTest]
	public void A_text_file_shows_read_only_with_its_path()
	{
		using var host = new PanelHost();

		host.Panel.Show(Document(@"C:\tools\run.ps1", "Write-Host hi"), host.Pane);
		host.Window.UpdateLayout();

		host.Popup.IsOpen.ShouldBeTrue();
		host.Panel.FindControl<TextBlock>("FileNameText")!.Text.ShouldBe("run.ps1");
		host.Panel.FindControl<SelectableTextBlock>("FullPathText")!.Text.ShouldBe(@"C:\tools\run.ps1");
		var editor = host.Panel.FindControl<SourceEditor>("TextView")!;
		editor.IsVisible.ShouldBeTrue();
		editor.IsReadOnly.ShouldBeTrue();
		editor.Text.ShouldBe("Write-Host hi");
		host.Panel.FindControl<MarkdownScrollViewer>("MarkdownView")!.IsVisible.ShouldBeFalse();
		host.Panel.FindControl<TextBlock>("PlaceholderText")!.IsVisible.ShouldBeFalse();
	}

	[AvaloniaTest]
	public void A_markdown_file_is_rendered()
	{
		using var host = new PanelHost();

		host.Panel.Show(Document(@"C:\notes\plan.md", "# Plan", markdown: true), host.Pane);
		host.Window.UpdateLayout();

		var viewer = host.Panel.FindControl<MarkdownScrollViewer>("MarkdownView")!;
		viewer.IsVisible.ShouldBeTrue();
		viewer.Markdown.ShouldBe("# Plan");
		host.Panel.FindControl<SourceEditor>("TextView")!.IsVisible.ShouldBeFalse();
	}

	[AvaloniaTest]
	public void Relative_images_draw_from_the_files_folder_outside_the_project()
	{
		// The headless bitmap keeps the drawn image open, so this folder is cleaned up best-effort.
		var first = Directory.CreateTempSubdirectory("pact-agent-file-").FullName;
		using var second = TemporaryDirectory.Create();
		try
		{
			File.WriteAllBytes(Path.Combine(first, "pic.png"), [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
			using var host = new PanelHost();
			var viewer = host.Panel.FindControl<MarkdownScrollViewer>("MarkdownView")!;

			host.Panel.Show(Document(Path.Combine(first, "notes.md"), "![pic](pic.png)", markdown: true), host.Pane);

			viewer.AssetPathRoot.ShouldBe(first);
			viewer.Markdown.ShouldBe("![pic](pic.png)");

			host.Panel.Show(Document(Path.Combine(second.Path, "other.md"), "text", markdown: true), host.Pane);

			viewer.AssetPathRoot.ShouldBe(second.Path);
		}
		finally
		{
			try
			{
				Directory.Delete(first, recursive: true);
			}
			catch (IOException)
			{
			}
		}
	}

	[AvaloniaTest]
	public void A_placeholder_shows_instead_of_text()
	{
		using var host = new PanelHost();

		host.Panel.Show(new AgentFileDocument(@"C:\x\a.bin", "a.bin", null, "Binary file, not shown", false, null, null), host.Pane);
		host.Window.UpdateLayout();

		var placeholder = host.Panel.FindControl<TextBlock>("PlaceholderText")!;
		placeholder.IsVisible.ShouldBeTrue();
		placeholder.Text.ShouldBe("Binary file, not shown");
		host.Panel.FindControl<SourceEditor>("TextView")!.IsVisible.ShouldBeFalse();
		host.Panel.FindControl<MarkdownScrollViewer>("MarkdownView")!.IsVisible.ShouldBeFalse();
	}

	[AvaloniaTest]
	public void The_panel_follows_the_center_pane_size()
	{
		using var host = new PanelHost();
		var border = host.Panel.FindControl<Border>("PanelBorder")!;

		host.Panel.Show(Document(@"C:\x\a.txt", "a"), host.Pane);
		host.Window.UpdateLayout();

		host.Popup.HorizontalOffset.ShouldBe(400);
		host.Popup.VerticalOffset.ShouldBe(0);
		(border.Width, border.Height).ShouldBe((600, 600));

		host.Pane.Width = 800;
		host.Pane.Height = 500;
		host.Window.UpdateLayout();

		host.Popup.HorizontalOffset.ShouldBe(320);
		(border.Width, border.Height).ShouldBe((480, 500));
	}

	[AvaloniaTest]
	public void Escape_and_the_cross_request_closing()
	{
		using var host = new PanelHost();
		var requests = 0;
		host.Panel.CloseRequested += (_, _) => requests++;
		host.Panel.Show(Document(@"C:\x\a.txt", "a"), host.Pane);
		host.Window.UpdateLayout();

		host.Panel.FindControl<Button>("CloseButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		host.Panel.FindControl<Border>("PanelBorder")!.RaiseEvent(new KeyEventArgs
		{
			RoutedEvent = InputElement.KeyDownEvent,
			Key = Key.Escape
		});

		requests.ShouldBe(2);
	}

	[AvaloniaTest]
	public void Escape_right_after_opening_from_a_focused_button_closes()
	{
		using var host = new PanelHost();
		Button opener = new() { Content = "open" };
		host.Pane.Children.Add(opener);
		host.Window.UpdateLayout();
		opener.Focus();
		var requests = 0;
		host.Panel.CloseRequested += (_, _) => requests++;

		host.Panel.Show(Document(@"C:\x\a.txt", "a"), host.Pane);
		Dispatcher.UIThread.RunJobs();
		host.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
		Dispatcher.UIThread.RunJobs();

		requests.ShouldBe(1);
	}

	[AvaloniaTest]
	public void A_refused_link_shows_its_notice()
	{
		using var host = new PanelHost();
		using var folder = TemporaryDirectory.Create();
		host.Panel.Show(
			new AgentFileDocument(Path.Combine(folder.Path, "docs", "a.md"), "a.md", "[x](../../out.md)", null, true, folder.Path, "docs/a.md"),
			host.Pane);
		host.Window.UpdateLayout();
		var viewer = host.Panel.FindControl<MarkdownScrollViewer>("MarkdownView")!;

		((global::Markdown.Avalonia.Markdown)viewer.Engine).HyperlinkCommand!.Execute("../../out.md");

		host.Panel.FindControl<Control>("LinkNotice")!.IsVisible.ShouldBeTrue();
		host.Panel.FindControl<TextBlock>("LinkNoticeText")!.Text.ShouldBe("Outside the project: ../../out.md");
	}

	private static AgentFileDocument Document(string path, string text, bool markdown = false) =>
		new(path, Path.GetFileName(path), text, null, markdown, null, null);

	private sealed class PanelHost : IDisposable
	{
		public PanelHost()
		{
			Panel = new AgentFilePanel();
			Pane = new Grid { Width = 1000, Height = 600, Children = { Panel } };
			Window = new Window
			{
				Width = 1200,
				Height = 800,
				Content = Pane,
				// The headless platform has no popup windows; popups go to this overlay layer.
				Template = new FuncControlTemplate<Window>((owner, scope) =>
				{
					ContentPresenter presenter = new()
					{
						[!ContentPresenter.ContentProperty] = owner[!ContentControl.ContentProperty]
					};
					VisualLayerManager layers = new() { Name = "PART_VisualLayerManager", Child = presenter };
					scope.Register(layers.Name, layers);
					return layers;
				})
			};
			Window.Show();
			Window.UpdateLayout();
			Popup = Panel.FindControl<Popup>("FilePopup")!;
		}

		public AgentFilePanel Panel { get; }

		public Grid Pane { get; }

		public Window Window { get; }

		public Popup Popup { get; }

		public void Dispose()
		{
			Panel.Hide();
			Window.Close();
		}
	}
}
