using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Markdown.Avalonia.Full;
using Pact.Presentation.Services;

namespace Pact.App.Avalonia.Views;

/// <summary>
/// Makes a Markdown preview follow its links inside Pact and draw its images from the document's
/// folder. The engine's own link command started the bare href as a process on the UI thread, which
/// could not find a relative file and threw out of the message loop.
/// </summary>
internal static partial class MarkdownPreviewHook
{
	/// <summary>Routes the viewer's links: resolved against the document it shows, then handed to <paramref name="navigate"/>.</summary>
	public static void Attach(
		MarkdownScrollViewer viewer,
		Func<(string ProjectRoot, string DocumentRelativePath)?> document,
		Action<MarkdownLinkTarget> navigate)
	{
		ArgumentNullException.ThrowIfNull(viewer);
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(navigate);
		if (viewer.Engine is global::Markdown.Avalonia.Markdown engine)
		{
			engine.HyperlinkCommand = new LinkCommand(href => navigate(document() is { } shown
				? MarkdownLinks.Resolve(shown.ProjectRoot, shown.DocumentRelativePath, href)
				: new RefusedLink($"Not found: {href}")));
		}
	}

	/// <summary>Draws relative images from the folder of the document shown; set before its text.</summary>
	public static void SetDocument(MarkdownScrollViewer viewer, string? projectRoot, string? documentRelativePath)
	{
		ArgumentNullException.ThrowIfNull(viewer);
		var folder = projectRoot is null || documentRelativePath is null
			? null
			: Path.GetDirectoryName(Path.Combine(projectRoot, documentRelativePath.Replace('/', Path.DirectorySeparatorChar)));
		if (!string.Equals(viewer.AssetPathRoot, folder, StringComparison.OrdinalIgnoreCase))
		{
			viewer.AssetPathRoot = folder;
		}
	}

	/// <summary>
	/// The text to show for a document in <paramref name="folder"/>: an image whose local file is
	/// missing becomes its alt text, since the engine would draw only a broken-image mark.
	/// </summary>
	public static string Prepare(string markdown, string? folder)
	{
		ArgumentNullException.ThrowIfNull(markdown);
		return folder is null
			? markdown
			: ImagePattern().Replace(markdown, match =>
			{
				var source = Uri.UnescapeDataString(match.Groups["source"].Value);
				if (source.Contains("://", StringComparison.Ordinal) || source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
				{
					return match.Value;
				}

				var path = Path.Combine(folder, source.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
				if (File.Exists(path))
				{
					return match.Value;
				}

				var alt = match.Groups["alt"].Value;
				return alt.Length > 0 ? $"*{alt}*" : $"*(missing image: {source})*";
			});
	}

	[GeneratedRegex(@"!\[(?<alt>[^\]]*)\]\((?<source>[^)\s]+)(\s+""[^""]*"")?\)")]
	private static partial Regex ImagePattern();

	/// <summary>Opens a web link in the system browser, off the UI thread.</summary>
	public static void OpenWeb(Uri uri)
	{
		ArgumentNullException.ThrowIfNull(uri);
		_ = Task.Run(() =>
		{
			try
			{
				using var process = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
			}
			catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
			{
				// No browser to open it in; nothing in Pact depends on it.
			}
		});
	}

	/// <summary>
	/// Brings the heading under <paramref name="root"/> whose slug matches <paramref name="heading"/>
	/// to the top of the scroller around it.
	/// </summary>
	/// <returns><see langword="false"/> when there is no such heading.</returns>
	public static bool ScrollToHeading(Control root, string heading)
	{
		ArgumentNullException.ThrowIfNull(root);
		ArgumentNullException.ThrowIfNull(heading);
		var wanted = Slug(heading);
		var target = global::Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root)
			.OfType<Control>()
			.FirstOrDefault(control => control.Classes.Any(name => name.StartsWith("Heading", StringComparison.Ordinal))
				&& control.GetType().GetProperty("Text")?.GetValue(control) is string text
				&& Slug(text) == wanted);
		if (target is null)
		{
			return false;
		}

		// The nearest scroller that has somewhere to go: the preview's own, or the one around
		// rendered blocks, whose inner viewers never scroll.
		var scroller = global::Avalonia.VisualTree.VisualExtensions.GetVisualAncestors(target)
			.OfType<ScrollViewer>()
			.FirstOrDefault(candidate => candidate.Extent.Height > candidate.Viewport.Height + 1);
		if (scroller?.Content is Control content && target.TranslatePoint(default, content) is { } at)
		{
			scroller.Offset = scroller.Offset.WithY(at.Y);
		}
		else
		{
			target.BringIntoView();
		}

		return true;
	}
	// As heading anchors are written: lower case, spaces to hyphens, other punctuation dropped.
	private static string Slug(string text)
	{
		StringBuilder slug = new();
		foreach (var character in text.Trim().ToLowerInvariant())
		{
			if (char.IsLetterOrDigit(character) || character is '-' or '_')
			{
				slug.Append(character);
			}
			else if (character == ' ')
			{
				slug.Append('-');
			}
		}

		return slug.ToString();
	}

	private sealed class LinkCommand(Action<string> follow) : ICommand
	{
		public event EventHandler? CanExecuteChanged
		{
			add { }
			remove { }
		}

		public bool CanExecute(object? parameter) => parameter is string;

		public void Execute(object? parameter)
		{
			if (parameter is string href)
			{
				follow(href);
			}
		}
	}
}
