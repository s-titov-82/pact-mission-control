namespace Pact.Core.Boards;

/// <summary>Shared input limits keep active summaries and individual card responses bounded.</summary>
public static class BoardLimits
{
	/// <summary>Maximum nonarchived cards per project.</summary>
	public const int ActiveCards = 200;
	/// <summary>Maximum title length in UTF-16 code units.</summary>
	public const int Title = 256;
	/// <summary>Maximum description length in UTF-16 code units.</summary>
	public const int Description = 16_384;
	/// <summary>Maximum comment length in UTF-16 code units.</summary>
	public const int Comment = 4_096;
	/// <summary>Maximum issue address length.</summary>
	public const int JiraUrl = 2_048;
}
