using System;

namespace Anvil.ViewModels
{
	/// <summary>
	/// Shared helpers for the "next update" progress bars (radar live frame + SPC outlook): the
	/// elapsed fraction of the current wait and a human countdown to the next update. Kept in one
	/// place so the radar and outlook view models can't drift apart.
	/// </summary>
	internal static class NextUpdate
	{
		/// <summary>Elapsed fraction (0..100) of the current wait — 0 right after an update, ~100 just
		/// before the next. Returns 0 when nothing is scheduled (e.g. between cycles).</summary>
		public static double ProgressOf(DateTimeOffset? start, DateTimeOffset? next)
		{
			if (start is not { } s || next is not { } n) return 0;
			var total = (n - s).TotalSeconds;
			if (total <= 0) return 0;
			return Math.Clamp((DateTimeOffset.Now - s).TotalSeconds / total * 100.0, 0, 100);
		}

		/// <summary>Human countdown to the next update, e.g. "next ~12s" / "next ~9 min" / "updating…".</summary>
		public static string CountdownOf(DateTimeOffset? next)
		{
			if (next is not { } n) return "";
			var rem = (n - DateTimeOffset.Now).TotalSeconds;
			if (rem <= 0) return "updating…";
			return rem >= 90 ? $"next ~{rem / 60:0} min" : $"next ~{rem:0}s";
		}
	}
}
