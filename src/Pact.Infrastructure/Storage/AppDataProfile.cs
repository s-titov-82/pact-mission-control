namespace Pact.Infrastructure.Storage;

/// <summary>
/// The resolved data root the application runs against.
/// </summary>
/// <param name="Name">
/// Profile label used in diagnostics to distinguish the stable profile from an isolated test one.
/// </param>
/// <param name="RootDirectory">
/// Absolute path holding the four runtime directories (<c>Settings</c>, <c>WebView</c>,
/// <c>Logs</c>, <c>Temp</c>). Only one process may hold a given root at a time.
/// </param>
public sealed record AppDataProfile(string Name, string RootDirectory);