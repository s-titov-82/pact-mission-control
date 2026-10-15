namespace Pact.Core.ScreenVerdictProfiles;

/// <summary>A terminal cell in the 1-based coordinates mouse reports use.</summary>
/// <param name="Column">1-based column.</param>
/// <param name="Row">1-based row of the live screen.</param>
public readonly record struct TerminalCellPosition(int Column, int Row);
