using System.Text.Json;
using System.Text.Json.Serialization;
using Pact.Infrastructure.Storage;

namespace Pact.Infrastructure.Settings;

/// <summary>
/// Saved window geometry and splitter positions, restored on the next launch.
/// </summary>
/// <param name="Left">Window left edge in screen coordinates.</param>
/// <param name="Top">Window top edge in screen coordinates.</param>
/// <param name="Width">Window width.</param>
/// <param name="Height">Window height.</param>
/// <param name="WindowState">Serialized window state, such as normal or maximized.</param>
/// <param name="LeftColumnWidth">Width of the project tree column.</param>
/// <param name="RightColumnWidth">Width of the actions column.</param>
public sealed record AppWindowLayout(
	double Left,
	double Top,
	double Width,
	double Height,
	string WindowState,
	double LeftColumnWidth,
	double RightColumnWidth)
{
	/// <summary>
	/// Whether the layout is large enough and numerically sane to apply. A layout failing this
	/// check is discarded in favor of defaults, so a corrupt file cannot produce an unusable or
	/// invisible window.
	/// </summary>
	[JsonIgnore]
	public bool IsUsable =>
		IsUsableWindowDimension(Width)
		&& IsUsableWindowDimension(Height)
		&& IsUsableColumnWidth(LeftColumnWidth)
		&& IsUsableColumnWidth(RightColumnWidth);

	private static bool IsUsableWindowDimension(double value) =>
		!double.IsNaN(value) && !double.IsInfinity(value) && value >= 200;

	private static bool IsUsableColumnWidth(double value) =>
		!double.IsNaN(value) && !double.IsInfinity(value) && value >= 100;
}

/// <summary>
/// Persists the window layout between runs.
/// </summary>
public sealed class WindowLayoutStore
{
	private readonly string _path;
	private readonly string? _stagingDirectory;

	/// <summary>
	/// Creates a store over <paramref name="path"/>, staging atomic writes in
	/// <paramref name="stagingDirectory"/> when supplied.
	/// </summary>
	public WindowLayoutStore(string path, string? stagingDirectory = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);

		_path = path;
		_stagingDirectory = stagingDirectory;
	}

	/// <summary>Creates a store over the standard path layout.</summary>
	public WindowLayoutStore(AppPaths paths)
		: this(Require(paths).WindowLayoutPath, Require(paths).AtomicTempDirectory)
	{
	}

	// A constructor initializer runs before the body, so the null check cannot be a
	// statement here; routing the argument through this guard keeps the failure an
	// ArgumentNullException naming the parameter.
	private static AppPaths Require(AppPaths paths)
	{
		ArgumentNullException.ThrowIfNull(paths);
		return paths;
	}

	/// <summary>
	/// Reads the saved layout synchronously, so startup can position the window before showing it.
	/// </summary>
	/// <returns>
	/// The layout, or <see langword="null"/> when none is saved or the saved one fails
	/// <see cref="AppWindowLayout.IsUsable"/>. Unreadable files return null rather than throwing.
	/// </returns>
	public AppWindowLayout? Load()
	{
		if (!File.Exists(_path))
		{
			return null;
		}

		AppWindowLayout? layout;
		try
		{
			layout = JsonSerializer.Deserialize<AppWindowLayout>(
				File.ReadAllText(_path),
				SettingsFileStore.JsonOptions);
		}
		catch (JsonException)
		{
			return null;
		}

		return layout?.IsUsable == true
			? layout
			: null;
	}

	/// <summary>Writes the layout atomically.</summary>
	public async Task SaveAsync(AppWindowLayout layout, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(layout);

		if (!layout.IsUsable)
		{
			return;
		}

		var json = JsonSerializer.Serialize(layout, SettingsFileStore.JsonOptions);
		await AtomicFileWriter.WriteTextAsync(_path, json, _stagingDirectory, cancellationToken)
			.ConfigureAwait(false);
	}
}
