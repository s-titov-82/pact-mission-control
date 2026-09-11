namespace Pact.Core.Updates;

/// <summary>Identifies whether a handoff restarts Pact only or also applies a verified update.</summary>
public enum SoftRestartMode
{
	/// <summary>Runs Setup before relaunching Pact.</summary>
	ApplyUpdate,
	/// <summary>Relaunches Pact without invoking Setup.</summary>
	RestartOnly
}
