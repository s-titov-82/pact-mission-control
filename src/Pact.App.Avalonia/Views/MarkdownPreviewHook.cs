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
		Action<MarkdownLinkTarget> navigate,
		bool historical = false)
	{
		ArgumentNullException.ThrowIfNull(viewer);
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(navigate);
		if (historical)
		{ viewer.Plugins.PathResolver = new HistoricalImageResolver(); }
		if (viewer.Engine is global::Markdown.Avalonia.Markdown engine)
		{
			engine.HyperlinkCommand = new LinkCommand(href =>
			{
				var shown = document();
				navigate(ResolveLink(shown?.ProjectRoot, shown?.DocumentRelativePath, href, historical));
			});
		}
	}

	/// <summary>Historical previews only follow same-document headings and HTTP(S), without probing local files.</summary>
	public static MarkdownLinkTarget ResolveLink(string? root, string? path, string href, bool historical)
	{
		if (historical && !href.TrimStart().StartsWith('#')
			&& !(Uri.TryCreate(href, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"))
		{ return new RefusedLink("Local file links are unavailable in commit view."); }
		return MarkdownLinks.Resolve(root, path, href);
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
	/// missing or does not start as an image file does becomes its alt text, since the engine would
	/// draw only a broken-image mark. Code, fenced, indented or inline, is left as written. A file
	/// that starts as an image but is damaged further on, and a web image that fails to load, still
	/// show the engine's mark.
	/// </summary>
	public static string Prepare(string markdown, string? folder, bool historical = false)
	{
		ArgumentNullException.ThrowIfNull(markdown);
		if ((!historical && folder is null) || (!markdown.Contains("![", StringComparison.Ordinal)
			&& (!historical || (!markdown.Contains("<img", StringComparison.OrdinalIgnoreCase)
				&& !markdown.Contains("<image", StringComparison.OrdinalIgnoreCase)))))
		{
			return markdown;
		}

		// Fenced and indented code is kept whole; the rest is taken a paragraph at a time, since a
		// code span may run over lines but never past a blank one.
		var lines = markdown.Split('\n');
		List<string> output = [];
		List<string> paragraph = [];
		(char Character, int Length)? fence = null;
		var previousBlank = true;
		var inIndentedCode = false;
		void Flush()
		{
			if (paragraph.Count > 0)
			{
				output.Add(OutsideCodeSpans(string.Join('\n', paragraph), text => ReplaceMissingImages(text, folder, historical)));
				paragraph.Clear();
			}
		}

		foreach (var line in lines)
		{
			var trimmed = line.TrimStart();
			var run = trimmed.Length > 0 && trimmed[0] is '`' or '~' ? trimmed.TakeWhile(character => character == trimmed[0]).Count() : 0;
			var blank = trimmed.Length == 0;
			if (fence is { } open)
			{
				output.Add(line);
				// As in Core's MarkdownBlocks: a closing fence is indented at most three spaces too.
				if (run >= open.Length && trimmed[0] == open.Character && trimmed.Trim().Length == run && Indent(line) <= 3)
				{
					fence = null;
				}
			}
			else if (run >= 3 && Indent(line) <= 3)
			{
				Flush();
				output.Add(line);
				fence = (trimmed[0], run);
			}
			else if (!blank && Indent(line) >= 4 && (previousBlank || inIndentedCode))
			{
				Flush();
				output.Add(line);
				inIndentedCode = true;
			}
			else if (blank)
			{
				Flush();
				output.Add(line);
			}
			else
			{
				inIndentedCode = false;
				paragraph.Add(line);
			}

			previousBlank = blank;
			if (!blank && Indent(line) < 4)
			{
				inIndentedCode = false;
			}
		}

		Flush();
		return string.Join('\n', output);
	}

	private static int Indent(string line) =>
		line.TakeWhile(character => character is ' ' or '\t').Sum(character => character == '\t' ? 4 : 1);

	// A code span opens with a run of backticks and closes at the next run of the same length; a
	// run that is never closed is plain text. Only the text outside spans is replaced.
	private static string OutsideCodeSpans(string text, Func<string, string> replace)
	{
		System.Text.StringBuilder result = new();
		var plainStart = 0;
		var index = 0;
		while (index < text.Length)
		{
			if (text[index] != '`')
			{
				index++;
				continue;
			}

			var length = RunLength(text, index);
			var close = FindRun(text, index + length, length);
			if (close < 0)
			{
				index += length;
				continue;
			}

			result.Append(replace(text[plainStart..index]));
			result.Append(text, index, close + length - index);
			index = close + length;
			plainStart = index;
		}

		result.Append(replace(text[plainStart..]));
		return result.ToString();
	}

	private static int RunLength(string text, int start)
	{
		var end = start;
		while (end < text.Length && text[end] == '`')
		{
			end++;
		}

		return end - start;
	}

	private static int FindRun(string text, int from, int length)
	{
		for (var index = from; index < text.Length; index++)
		{
			if (text[index] != '`')
			{
				continue;
			}

			var run = RunLength(text, index);
			if (run == length)
			{
				return index;
			}

			index += run - 1;
		}

		return -1;
	}

	private static string ReplaceMissingImages(string text, string? folder, bool historical)
	{
		var replaced = ImagePattern().Replace(text, match =>
		{
			var source = Uri.UnescapeDataString(match.Groups["angle"].Success ? match.Groups["angle"].Value : match.Groups["source"].Value);
			if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "data")
			{
				return match.Value;
			}

			if (!historical && folder is not null && CanDecode(Path.Combine(folder, source.TrimStart('/').Replace('/', Path.DirectorySeparatorChar))))
			{
				return match.Value;
			}

			var alt = match.Groups["alt"].Value;
			if (historical)
			{ return HistoricalImage(alt); }
			return alt.Length > 0 ? $"*{alt}*" : $"*(missing image: {source})*";
		});
		// Reference and shortcut images must not ask the engine to load a current local asset either.
		if (!historical)
		{ return replaced; }
		replaced = ReferenceImagePattern().Replace(replaced, match => HistoricalImage(match.Groups["alt"].Value));
		return HtmlImagePattern().Replace(replaced, match =>
		{
			string Attribute(string name)
			{
				var attribute = HtmlImageAttributePattern().Matches(match.Value).FirstOrDefault(item => string.Equals(item.Groups["name"].Value, name, StringComparison.OrdinalIgnoreCase));
				return attribute is null ? string.Empty : System.Net.WebUtility.HtmlDecode(attribute.Groups["double"].Success ? attribute.Groups["double"].Value
					: attribute.Groups["single"].Success ? attribute.Groups["single"].Value : attribute.Groups["bare"].Value);
			}
			return Uri.TryCreate(Attribute("src"), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "data"
				? match.Value : HistoricalImage(Attribute("alt"));
		});
	}

	private static string HistoricalImage(string alt) => $"*{(alt.Length > 0 ? alt : "Image")} (image unavailable in commit view)*";
	[GeneratedRegex(@"!\[(?<alt>[^\]]*)\](?!\()(?:\[[^\]]*\])?")]
	private static partial Regex ReferenceImagePattern();
	[GeneratedRegex(@"<(?:img|image)\b(?:""[^""]*""|'[^']*'|[^'"">])*>", RegexOptions.IgnoreCase)]
	private static partial Regex HtmlImagePattern();
	[GeneratedRegex(@"(?<name>src|alt)\s*=\s*(?:""(?<double>[^""]*)""|'(?<single>[^']*)'|(?<bare>[^\s>]+))", RegexOptions.IgnoreCase)]
	private static partial Regex HtmlImageAttributePattern();

	// A file the engine can draw starts as an image does; anything else would show a broken mark.
	private static bool CanDecode(string path)
	{
		try
		{
			using var stream = File.OpenRead(path);
			Span<byte> head = stackalloc byte[16];
			var read = stream.Read(head);
			return LooksLikeImage(head[..read]);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
		{
			return false;
		}
	}

	private static bool LooksLikeImage(ReadOnlySpan<byte> head) =>
		head.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47])
		|| head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF])
		|| head.StartsWith("GIF8"u8)
		|| head.StartsWith("BM"u8)
		|| (head.Length >= 12 && head.StartsWith("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8))
		|| head.StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0x01, 0x00])
		|| head.TrimStart((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF, 0x20, 0x09, 0x0D, 0x0A]).StartsWith("<"u8);
	[GeneratedRegex(@"!\[(?<alt>[^\]]*)\]\((?:<(?<angle>[^>]+)>|(?<source>[^)\s]+))(\s+(?:""[^""]*""|'[^']*'))?\)")]
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

	// Enforce the resource policy at the loader too: plugin-supported image syntaxes must never
	// fall through to the working tree when preprocessing cannot recognize their destination.
	private sealed class HistoricalImageResolver : global::Markdown.Avalonia.Utils.IPathResolver
	{
		private readonly global::Markdown.Avalonia.Utils.DefaultPathResolver _web = new();
		public string? AssetPathRoot { set => _web.AssetPathRoot = value; }
		public IEnumerable<string>? CallerAssemblyNames { set => _web.CallerAssemblyNames = value; }
		public Task<Stream?>? ResolveImageResource(string relativeOrAbsolutePath) =>
			Uri.TryCreate(relativeOrAbsolutePath, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "data"
				? _web.ResolveImageResource(relativeOrAbsolutePath) : Task.FromResult<Stream?>(null);
	}
}
