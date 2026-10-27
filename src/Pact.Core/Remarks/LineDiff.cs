namespace Pact.Core.Remarks;

/// <summary>A minimal line diff (Myers) that tells which lines of one text survive unchanged in another.</summary>
public static class LineDiff
{
	/// <summary>
	/// Maps every line of <paramref name="before"/> to its index in <paramref name="after"/> when it is
	/// unchanged in a minimal line diff, or to <see langword="null"/> when it was deleted or changed.
	/// </summary>
	/// <param name="before">Lines of the older text.</param>
	/// <param name="after">Lines of the newer text.</param>
	/// <param name="maxEdits">
	/// Most insertions plus deletions worth computing; the trace kept for the answer grows with
	/// their square, so a larger difference gives up instead of stalling.
	/// </param>
	/// <returns>The map, or <see langword="null"/> when the texts differ by more than <paramref name="maxEdits"/> edits.</returns>
	public static int?[]? MapUnchanged(IReadOnlyList<string> before, IReadOnlyList<string> after, int maxEdits = 2000)
	{
		ArgumentNullException.ThrowIfNull(before);
		ArgumentNullException.ThrowIfNull(after);
		var map = new int?[before.Count];
		var prefix = 0;
		while (prefix < before.Count && prefix < after.Count && string.Equals(before[prefix], after[prefix], StringComparison.Ordinal))
		{
			map[prefix] = prefix;
			prefix++;
		}

		var suffix = 0;
		while (suffix < before.Count - prefix && suffix < after.Count - prefix
			&& string.Equals(before[before.Count - 1 - suffix], after[after.Count - 1 - suffix], StringComparison.Ordinal))
		{
			map[before.Count - 1 - suffix] = after.Count - 1 - suffix;
			suffix++;
		}

		var n = before.Count - prefix - suffix;
		var m = after.Count - prefix - suffix;
		return MapMiddle(before, after, prefix, n, m, maxEdits, map) ? map : null;
	}

	// Myers' greedy forward search over the middle part, keeping each round's furthest points so the
	// path can be walked back and its diagonal runs (unchanged lines) recorded.
	private static bool MapMiddle(
		IReadOnlyList<string> before,
		IReadOnlyList<string> after,
		int offset,
		int n,
		int m,
		int maxEdits,
		int?[] map)
	{
		if (n == 0 || m == 0)
		{
			return n + m <= maxEdits;
		}

		var max = n + m;
		var v = new int[(2 * max) + 2];
		List<int[]> trace = [];
		for (var d = 0; d <= Math.Min(max, maxEdits); d++)
		{
			// Round d reads diagonals -d..d only, so that slice of the previous round is all the walk back needs.
			trace.Add(v[(max - d)..(max + d + 1)]);
			for (var k = -d; k <= d; k += 2)
			{
				var x = k == -d || (k != d && v[max + k - 1] < v[max + k + 1])
					? v[max + k + 1]
					: v[max + k - 1] + 1;
				var y = x - k;
				while (x < n && y < m && string.Equals(before[offset + x], after[offset + y], StringComparison.Ordinal))
				{
					x++;
					y++;
				}

				v[max + k] = x;
				if (x >= n && y >= m)
				{
					WalkBack(trace, n, m, d, offset, map);
					return true;
				}
			}
		}

		return false;
	}

	private static void WalkBack(List<int[]> trace, int n, int m, int edits, int offset, int?[] map)
	{
		var x = n;
		var y = m;
		for (var d = edits; d > 0; d--)
		{
			// trace[d] holds diagonals -d..d at index k + d.
			var v = trace[d];
			var k = x - y;
			var previousK = k == -d || (k != d && v[d + k - 1] < v[d + k + 1]) ? k + 1 : k - 1;
			var previousX = v[d + previousK];
			var previousY = previousX - previousK;
			while (x > previousX && y > previousY)
			{
				x--;
				y--;
				map[offset + x] = offset + y;
			}

			x = previousX;
			y = previousY;
		}

		while (x > 0 && y > 0)
		{
			x--;
			y--;
			map[offset + x] = offset + y;
		}
	}
}
