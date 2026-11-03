namespace Pact.Core.Platform;

/// <summary>An installed native editor; its optional PNG icon is retained only in memory.</summary>
/// <param name="Id">Stable installation identity, including its executable path.</param>
/// <param name="Name">Display name, qualified when several installations exist.</param>
/// <param name="ExecutablePath">Absolute path to the native executable, never a shell wrapper.</param>
/// <param name="IconPng">PNG bytes, or null when icon extraction is unavailable.</param>
public sealed record InstalledIde(string Id, string Name, string ExecutablePath, byte[]? IconPng);
