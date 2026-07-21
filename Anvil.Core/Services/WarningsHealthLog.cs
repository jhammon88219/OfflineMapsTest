using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Anvil.Services
{
	/// <summary>
	/// One north-star reconciliation for a single warning-refresh cycle: how many warnings the app is
	/// DISPLAYING vs how many the authoritative CAP feed (api.weather.gov, our render source) says are
	/// active vs the WWA mapservice cross-check — and the exact id sets that differ. This is the record
	/// the app checks "what NWS has" against "what we show".
	/// </summary>
	public sealed record WarningsHealth(
		DateTimeOffset Ts,
		int DisplayedCount,
		int PrimaryCount,        // CAP (api.weather.gov) — authoritative, what we render from
		int CrossCheckCount,     // WWA mapservice, or -1 if that cross-check fetch failed
		IReadOnlyList<string> MissingFromDisplay, // active per CAP but NOT shown — the dangerous case
		IReadOnlyList<string> ExtraInDisplay,     // shown but NOT active per CAP — stale
		IReadOnlyList<string> OnlyPrimary,        // CAP has it, WWA doesn't (WWA lagging/dropping)
		IReadOnlyList<string> OnlyCrossCheck,     // WWA has it, CAP doesn't
		string MergeClassification,               // what ApplyFetch did this cycle
		string Verdict,                           // OK | DISPLAY_DIVERGED | FETCH_FAILED
		string? Note = null);

	/// <summary>
	/// Persists the per-cycle warning north-star reconciliation so intermittent divergence is diagnosable
	/// after the fact (the freeform Debug.WriteLine lines vanish with the session). Best-effort — never
	/// throws into the refresh loop. Writes two artifacts to the given folder (the warnings cache dir):
	///   • <c>warnings-health-&lt;stamp&gt;.jsonl</c> — append-only, one JSON reconciliation per cycle
	///     (the source of truth; grep it for VERDICT != OK).
	///   • <c>warnings-health-latest.md</c> — rewritten each cycle: the current verdict + the last few
	///     cycles, for a human glance ("is what we show matching NWS right now?").
	/// </summary>
	public sealed class WarningsHealthLog
	{
		private readonly string? _jsonlPath;
		private readonly string? _latestPath;
		private readonly object _gate = new();
		private readonly LinkedList<string> _recent = new(); // last few one-line summaries for the .md
		private const int RecentKept = 12;

		private static readonly JsonSerializerOptions JsonOpts = new()
		{
			DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
		};

		public WarningsHealthLog(string dir)
		{
			try
			{
				Directory.CreateDirectory(dir);
				var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
				_jsonlPath = Path.Combine(dir, $"warnings-health-{stamp}.jsonl");
				_latestPath = Path.Combine(dir, "warnings-health-latest.md");
			}
			catch
			{
				// Leave the log inert rather than breaking construction.
				_jsonlPath = null;
				_latestPath = null;
			}
		}

		public void Write(WarningsHealth h)
		{
			if (_jsonlPath is null) return;
			try
			{
				var line = JsonSerializer.Serialize(h, JsonOpts);
				lock (_gate)
				{
					File.AppendAllText(_jsonlPath, line + "\n");
					PushRecent(OneLine(h));
					RewriteLatest(h);
				}
			}
			catch
			{
				// Diagnostics must never disrupt the refresh.
			}
		}

		private void PushRecent(string summary)
		{
			_recent.AddFirst(summary);
			while (_recent.Count > RecentKept) { _recent.RemoveLast(); }
		}

		private static string OneLine(WarningsHealth h)
		{
			var cross = h.CrossCheckCount < 0 ? "n/a" : h.CrossCheckCount.ToString(CultureInfo.InvariantCulture);
			var extras = new List<string>();
			if (h.MissingFromDisplay.Count > 0) { extras.Add($"MISSING={h.MissingFromDisplay.Count}"); }
			if (h.ExtraInDisplay.Count > 0) { extras.Add($"EXTRA={h.ExtraInDisplay.Count}"); }
			if (h.OnlyPrimary.Count > 0) { extras.Add($"CAP-only={h.OnlyPrimary.Count}"); }
			if (h.OnlyCrossCheck.Count > 0) { extras.Add($"WWA-only={h.OnlyCrossCheck.Count}"); }
			var tail = extras.Count > 0 ? " · " + string.Join(" ", extras) : "";
			return $"{h.Ts:HH:mm:ss} {h.Verdict,-16} shown={h.DisplayedCount} CAP={h.PrimaryCount} WWA={cross} [{h.MergeClassification}]{tail}";
		}

		private void RewriteLatest(WarningsHealth h)
		{
			if (_latestPath is null) return;
			var sb = new StringBuilder();
			sb.AppendLine("# Warnings north-star health");
			sb.AppendLine();
			sb.AppendLine($"**Latest:** {h.Ts:yyyy-MM-dd HH:mm:ss} — **{h.Verdict}**");
			sb.AppendLine();
			sb.AppendLine($"- Displayed (app): **{h.DisplayedCount}**");
			sb.AppendLine($"- CAP api.weather.gov (authoritative render source): **{h.PrimaryCount}**");
			sb.AppendLine($"- WWA mapservice (cross-check): **{(h.CrossCheckCount < 0 ? "unavailable" : h.CrossCheckCount.ToString(CultureInfo.InvariantCulture))}**");
			if (h.MissingFromDisplay.Count > 0) { sb.AppendLine($"- ⚠️ Active per CAP but NOT shown: {string.Join(", ", h.MissingFromDisplay)}"); }
			if (h.ExtraInDisplay.Count > 0) { sb.AppendLine($"- Shown but not active per CAP (stale): {string.Join(", ", h.ExtraInDisplay)}"); }
			if (h.OnlyPrimary.Count > 0) { sb.AppendLine($"- CAP has, WWA missing (WWA lagging): {h.OnlyPrimary.Count}"); }
			if (h.OnlyCrossCheck.Count > 0) { sb.AppendLine($"- WWA has, CAP missing: {h.OnlyCrossCheck.Count}"); }
			if (h.Note is not null) { sb.AppendLine($"- Note: {h.Note}"); }
			sb.AppendLine();
			sb.AppendLine("## Recent cycles");
			sb.AppendLine();
			sb.AppendLine("```");
			foreach (var r in _recent) { sb.AppendLine(r); }
			sb.AppendLine("```");
			File.WriteAllText(_latestPath, sb.ToString());
		}

		/// <summary>Pure reconciliation (unit-tested): compares the displayed id set against the CAP
		/// authoritative set and the optional WWA cross-check set, producing the verdict + difference
		/// lists. <paramref name="crossCheck"/> null = the WWA cross-check fetch failed this cycle.</summary>
		public static WarningsHealth Reconcile(
			DateTimeOffset ts,
			IEnumerable<string> displayed,
			IReadOnlyCollection<string> primary,
			IReadOnlyCollection<string>? crossCheck,
			string mergeClassification)
		{
			var disp = new HashSet<string>(displayed);
			var prim = new HashSet<string>(primary);

			var missing = prim.Where(id => !disp.Contains(id)).ToList();
			var extra = disp.Where(id => !prim.Contains(id)).ToList();

			var onlyPrimary = new List<string>();
			var onlyCross = new List<string>();
			var crossCount = -1;
			if (crossCheck is not null)
			{
				var cross = new HashSet<string>(crossCheck);
				onlyPrimary = prim.Where(id => !cross.Contains(id)).ToList();
				onlyCross = cross.Where(id => !prim.Contains(id)).ToList();
				crossCount = cross.Count;
			}

			var verdict = (missing.Count == 0 && extra.Count == 0) ? "OK" : "DISPLAY_DIVERGED";
			return new WarningsHealth(ts, disp.Count, prim.Count, crossCount,
				missing, extra, onlyPrimary, onlyCross, mergeClassification, verdict);
		}
	}
}
