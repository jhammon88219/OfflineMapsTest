using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OfflineMapsTest.Services
{
	/// <summary>
	/// Dedicated radar diagnostics service — the structured replacement for the old freeform
	/// <c>RadarDebugLog</c>. It exists to make intermittent radar issues tractable while we develop
	/// other features: every meaningful radar event (site select, frame load, live poll, decode
	/// metrics, render health) is recorded as a typed event from all three subsystems
	/// (vm / svc / js) and correlated by a per-click load-session id.
	///
	/// Two artifacts per app run, written to a package-local <c>Diagnostics/</c> folder and named by
	/// launch time (NEVER auto-deleted or truncated — the developer prunes them):
	///   • <c>radar-diag-&lt;stamp&gt;.jsonl</c> — append-only, one JSON object per event. The
	///     machine-readable source of truth; crash-safe (a torn last line loses only that line).
	///   • <c>radar-report-&lt;stamp&gt;.md</c> — a derived rolling summary (load timings, frame
	///     quality table, poll cadence, data-latency + decode aggregates, the anomaly list),
	///     regenerated on a timer and at shutdown for a human glance.
	/// A suspect frame (partial sweep, empty, bad dealias) also has its source <c>.V06</c> copied
	/// into <c>_suspect/&lt;stamp&gt;/</c> so the cache prune can't delete the evidence first.
	///
	/// Static + lockless-to-callers so any layer can record without DI plumbing (same rationale as
	/// the service it replaces). Designed so future tilts/products drop in with no pipeline change:
	/// events carry <c>elevNumber</c>/<c>tiltAngle</c>/<c>product</c> where known.
	/// </summary>
	public static class RadarDiagnostics
	{
		private static readonly object Gate = new();
		private static readonly StringBuilder Pending = new();
		private static long _seq;
		private static bool _started;
		private static string? _jsonlPath;
		private static string? _reportPath;
		private static string? _suspectDir;
		private static string _stamp = "";
		private static readonly DateTimeOffset LaunchTime = DateTimeOffset.Now;

		private static readonly Session Live = new();      // current/most-recent load session
		private static readonly RunStats Stats = new();    // run-wide aggregates

		private static readonly JsonSerializerOptions JsonOpts = new()
		{
			DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
		};

		/// <summary>
		/// Opens this run's diagnostic files under <paramref name="diagDir"/> and starts the
		/// background flush + report-regeneration task. Best-effort and idempotent: a failure to
		/// open the files leaves diagnostics inert rather than throwing into startup.
		/// </summary>
		public static void Init(string diagDir)
		{
			lock (Gate)
			{
				if (_started) return;
				_started = true;
				try
				{
					Directory.CreateDirectory(diagDir);
					_stamp = LaunchTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
					_jsonlPath = Path.Combine(diagDir, $"radar-diag-{_stamp}.jsonl");
					_reportPath = Path.Combine(diagDir, $"radar-report-{_stamp}.md");
					_suspectDir = Path.Combine(diagDir, "_suspect", _stamp);
					File.WriteAllText(_jsonlPath,
						$"{{\"ts\":\"{LaunchTime:O}\",\"seq\":0,\"sub\":\"app\",\"cat\":\"run.start\",\"stamp\":\"{_stamp}\"}}\n");
				}
				catch
				{
					_jsonlPath = null;
					_reportPath = null;
					return;
				}
			}

			_ = Task.Run(async () =>
			{
				while (true)
				{
					await Task.Delay(2000).ConfigureAwait(false);
					Flush();
					WriteReport();
				}
			});
		}

		// ── Generic primitive: write one JSONL event. Everything funnels through here. ──

		/// <summary>Records one event: an envelope (ts/seq/sub/cat + current site/session) plus the
		/// given fields, as a single JSON line. Safe from any thread.</summary>
		public static void Log(string sub, string cat, params (string Key, object? Value)[] fields)
		{
			var dict = new Dictionary<string, object?>(8 + fields.Length)
			{
				["ts"] = DateTimeOffset.Now.ToString("O"),
				["seq"] = Interlocked.Increment(ref _seq),
				["sub"] = sub,
				["cat"] = cat,
			};
			lock (Gate)
			{
				if (Live.Site is { } s) dict["site"] = s;
				if (Live.Id > 0) dict["sid"] = Live.Id;
				foreach (var (k, v) in fields)
				{
					if (v is not null) dict[k] = v;
				}
				WriteLine(dict);
			}
		}

		// ── Typed wrappers: emit a JSONL event AND update the in-memory report model. ──

		/// <summary>Starts a new load session for a site click (or clears it when id is null).
		/// Returns the new session id.</summary>
		public static long BeginSession(string? siteId)
		{
			lock (Gate)
			{
				if (siteId is null)
				{
					Live.Reset(null);
					Live.Id = ++Stats.SessionCounter;
					WriteEnvelope("vm", "session.clear");
					return Live.Id;
				}

				Live.Reset(siteId);
				Live.Id = ++Stats.SessionCounter;
				Live.ClickAt = DateTimeOffset.UtcNow;
				WriteEnvelope("vm", "session.start");
				return Live.Id;
			}
		}

		/// <summary>Records a load-timing milestone (first frame / all frames / first live) once.</summary>
		public static void Timing(string which, double seconds)
		{
			lock (Gate)
			{
				switch (which)
				{
					case "first": Live.FirstFrameSec ??= seconds; break;
					case "all": Live.AllFramesSec ??= seconds; break;
					case "live": Live.FirstLiveSec ??= seconds; break;
				}
				WriteEnvelope("vm", "timing", ("which", which), ("sec", Round(seconds)));
			}
		}

		/// <summary>Registers the on-disk source of a loop frame as it's added, so a later
		/// suspect flag can quarantine the exact <c>.V06</c> and we can sample its data latency.</summary>
		public static void RegisterFrameSource(int index, string source, string cacheFile, DateTimeOffset? volumeTimeZ)
		{
			lock (Gate)
			{
				var f = Live.Frame(index);
				f.Source = source;
				f.CacheFile = cacheFile;
				f.VolumeTimeZ = volumeTimeZ;
				double? lat = volumeTimeZ is { } v ? (DateTimeOffset.UtcNow - v).TotalSeconds : null;
				f.LatencySec = lat;
				if (lat is { } l) Stats.Latency.Add(l);
				// Headline freshness = the LIVE (chunks) frame's latency only. The live frame is the
				// freshness mechanism; each live register is a genuinely-newer sweep (skips don't
				// register). Excluding archive registrations keeps the one ~11-min archive-newest
				// sample at load from skewing this — that floor still shows in All-frames latency.
				if (source == "live" && lat is { } nl) Stats.NewestLatency.Add(nl);
				WriteEnvelope("vm", "frame.register",
					("idx", index), ("src", source),
					("volZ", volumeTimeZ?.ToUniversalTime().ToString("O")),
					("latSec", lat is { } x ? Round(x) : null));
			}
		}

		/// <summary>A loop frame finished decoding (from the VM's OnRadarFrameReady).</summary>
		public static void FrameReady(int index, bool hasData, int readyCount, int frameCount)
		{
			lock (Gate)
			{
				Live.Frame(index).HasData = hasData;
				Live.FrameCount = frameCount;
				// Count DISTINCT frames that have decoded, not the VM's cumulative ready tally — live
				// re-decodes re-fire OnRadarFrameReady for an already-ready slot, which is why the raw
				// count runs past the frame count. The raw tally is kept as readyEvents for context.
				Live.ReadyCount = Live.Frames.Count(kv => kv.Value.HasData.HasValue);
				WriteEnvelope("vm", "frame.ready",
					("idx", index), ("hasData", hasData),
					("ready", Live.ReadyCount), ("total", frameCount), ("readyEvents", readyCount));
			}
		}

		/// <summary>Records a live-frame poll outcome + cadence (interval since the previous poll).</summary>
		public static void LivePoll(string result, DateTimeOffset? volumeTimeZ, string? mode)
		{
			lock (Gate)
			{
				var now = DateTimeOffset.Now;
				double? interval = Live.LastPollAt is { } p ? (now - p).TotalSeconds : null;
				if (interval is { } iv) Stats.PollIntervals.Add(iv);
				Live.LastPollAt = now;
				Live.PollCount++;
				if (result.StartsWith("ok", StringComparison.Ordinal)) Live.PollOkCount++;
				if (mode is not null) Live.Mode = mode;
				WriteEnvelope("vm", "live.poll",
					("result", result),
					("volZ", volumeTimeZ?.ToUniversalTime().ToString("O")),
					("mode", mode),
					("intervalSec", interval is { } x ? Round(x) : null));
			}
		}

		/// <summary>Routes a structured <c>radarFrame</c> message from radar.js: records the decode
		/// metrics, evaluates the suspect heuristics, and quarantines the source on a flag.</summary>
		public static void JsFrame(JsonElement msg)
		{
			lock (Gate)
			{
				int idx = GetInt(msg, "index") ?? -1;
				bool empty = GetBool(msg, "empty") ?? false;
				int tris = GetInt(msg, "tris") ?? 0;
				int velTris = GetInt(msg, "velTris") ?? 0;
				double reflSpan = GetSpan(msg, "reflStats");
				double velSpan = GetSpan(msg, "velStats");
				int velGates = GetStatGates(msg, "velStats");
				bool hasVel = velTris > 0;
				(int hi, int total) = ParseDealiasHi(GetStr(msg, "dealias"));

				var reasons = new List<string>();
				if (empty) reasons.Add("empty");
				else
				{
					if (tris <= 0) reasons.Add("no refl gates");
					else if (reflSpan > 0 && reflSpan < 330) reasons.Add($"refl partial sweep ({reflSpan:0}°)");
					if (hasVel)
					{
						if (velSpan > 0 && velSpan < 300) reasons.Add($"vel wedge ({velSpan:0}°)");
						if (velGates == 0) reasons.Add("vel zero gates");
						if (total > 0 && hi > 0 && (double)hi / total > 0.02) reasons.Add($"dealias hi {hi}/{total}");
					}
				}

				var f = Live.Frame(idx);
				f.Tris = tris;
				f.VelTris = velTris;
				f.ReflSpan = reflSpan;
				f.VelSpan = velSpan;
				f.DecodeMs = GetDouble(msg, "decodeMs");
				f.Suspect = reasons.Count > 0;
				f.SuspectReason = reasons.Count > 0 ? string.Join("; ", reasons) : null;
				if (f.DecodeMs is { } dm) Stats.DecodeMs.Add(dm);
				Stats.FramesDecoded++;

				// The raw JS payload is nested under "js" so the JSONL keeps every metric losslessly.
				// A "lvl" of warn marks suspect frames so the machine log is filterable by severity.
				WriteEnvelope("js", "frame",
					("lvl", f.Suspect ? "warn" : null),
					("idx", idx), ("suspect", f.Suspect ? f.SuspectReason : null), ("js", (object)msg.Clone()));

				if (f.Suspect)
				{
					Stats.SuspectCount++;
					Live.Anomalies.Add(new Anomaly(DateTimeOffset.Now, idx, f.SuspectReason ?? "?", QuarantineFrame(f)));
				}
			}
		}

		/// <summary>Routes a structured <c>radarRender</c> message (blank / error / recovered /
		/// context lost-restored) from radar.js.</summary>
		public static void JsRender(JsonElement msg)
		{
			lock (Gate)
			{
				var kind = GetStr(msg, "kind") ?? "?";
				// radar.js rate-limits these and carries running totals, so take the max rather than
				// incrementing (an increment would undercount the suppressed repeats).
				if (GetInt(msg, "errs") is { } errs) Stats.RenderErrors = Math.Max(Stats.RenderErrors, errs);
				if (GetInt(msg, "blanks") is { } blanks) Stats.RenderBlanks = Math.Max(Stats.RenderBlanks, blanks);
				// Severity for the machine log: a blank/error frame or lost GL context is worth flagging;
				// "recovered"/"restored" are just info.
				string? lvl = kind is "error" or "blank" ? "error" : kind is "ctxlost" ? "warn" : null;
				WriteEnvelope("js", "render", ("lvl", lvl), ("kind", kind), ("js", (object)msg.Clone()));
			}
		}

		/// <summary>A free-form line from radar.js (beginLoop/addFrame/showFrame/layer add-remove
		/// and the WebGL context edges) — kept as a string event for forensic context.</summary>
		public static void JsLog(string msg)
		{
			lock (Gate) WriteEnvelope("js", "log", ("msg", msg));
		}

		// ── Quarantine ──

		// Copies a suspect frame's cached .V06 into _suspect/<stamp>/ so the cache prune can't delete
		// the evidence before it's inspected. Returns the copied file name (or null on failure).
		private static string? QuarantineFrame(FrameRec f)
		{
			if (_suspectDir is null || f.CacheFile is null || !File.Exists(f.CacheFile)) return null;
			try
			{
				Directory.CreateDirectory(_suspectDir);
				var dest = Path.Combine(_suspectDir, Path.GetFileName(f.CacheFile));
				if (!File.Exists(dest)) File.Copy(f.CacheFile, dest);
				return Path.GetFileName(dest);
			}
			catch
			{
				return null;
			}
		}

		// ── File output ──

		private static void WriteEnvelope(string sub, string cat, params (string Key, object? Value)[] fields)
		{
			// Caller holds Gate. Builds the same envelope as Log() but without re-locking.
			var dict = new Dictionary<string, object?>(8 + fields.Length)
			{
				["ts"] = DateTimeOffset.Now.ToString("O"),
				["seq"] = Interlocked.Increment(ref _seq),
				["sub"] = sub,
				["cat"] = cat,
			};
			if (Live.Site is { } s) dict["site"] = s;
			if (Live.Id > 0) dict["sid"] = Live.Id;
			foreach (var (k, v) in fields)
			{
				if (v is not null) dict[k] = v;
			}
			WriteLine(dict);
		}

		// Caller holds Gate. Serializes the event and queues it for the background flush.
		private static void WriteLine(Dictionary<string, object?> dict)
		{
			if (_jsonlPath is null) return;
			try
			{
				Pending.Append(JsonSerializer.Serialize(dict, JsonOpts)).Append('\n');
			}
			catch
			{
				// A value that won't serialize must never break logging.
			}
		}

		/// <summary>Writes any queued JSONL lines to disk (no-op when the sink is off).</summary>
		public static void Flush()
		{
			string path; string text;
			lock (Gate)
			{
				if (_jsonlPath is null || Pending.Length == 0) return;
				path = _jsonlPath;
				text = Pending.ToString();
				Pending.Clear();
			}
			try { File.AppendAllText(path, text); }
			catch { /* transient lock; next flush retries */ }
		}

		/// <summary>Regenerates the human-readable <c>.md</c> report from the in-memory model.</summary>
		public static void WriteReport()
		{
			string path; string text;
			lock (Gate)
			{
				if (_reportPath is null) return;
				path = _reportPath;
				text = RenderReport();
			}
			try { File.WriteAllText(path, text); }
			catch { /* transient lock; next regen retries */ }
		}

		/// <summary>Flush + report regeneration for shutdown.</summary>
		public static void FlushAll()
		{
			Flush();
			WriteReport();
		}

		// Caller holds Gate. Builds the human-readable report: a plain-English health verdict, a
		// glossary so the terms below need no outside knowledge, each aggregate annotated with
		// whether it looks fine, the current loop in prose + a table with a plain Status column, and
		// a problems list. The raw quantiles are kept on each line (in [brackets]) for precise reading.
		private static string RenderReport()
		{
			var sb = new StringBuilder(4096);
			var now = DateTimeOffset.Now;

			// ── Headline: a one-glance verdict + summary, before any jargon. ──
			int pollFails = Live.PollCount - Live.PollOkCount;
			bool problems = Stats.RenderErrors > 0 || Stats.RenderBlanks > 0;
			bool warnings = Live.Anomalies.Count > 0 || pollFails > 0;
			string verdict = problems ? "PROBLEMS" : warnings ? "WARNINGS" : "OK";
			double? liveAge = Live.Frames.Values.Where(f => f.Source == "live")
				.Select(f => f.LatencySec).LastOrDefault(v => v.HasValue);

			sb.AppendLine($"# Radar diagnostics — run {_stamp}");
			sb.AppendLine();
			sb.AppendLine($"**Health: {verdict}**");
			sb.AppendLine();
			sb.AppendLine($"> {BuildSummary(liveAge, pollFails)}");
			sb.AppendLine();
			sb.AppendLine($"- Launched: {LaunchTime:yyyy-MM-dd HH:mm:ss} (local) · Uptime: {(now - LaunchTime).TotalMinutes:0.0} min");
			sb.AppendLine($"- Logged events: {Interlocked.Read(ref _seq)} · Site selections (sessions): {Stats.SessionCounter}");
			sb.AppendLine();

			// ── Glossary: makes the rest of the report self-explanatory. ──
			sb.AppendLine("## What the terms mean");
			sb.AppendLine();
			sb.AppendLine("- **Session** — one radar-site selection. Everything resets when you pick a new site.");
			sb.AppendLine("- **Frame** — one radar image in the animated loop (one volume scan). Newest = \"live\".");
			sb.AppendLine("- **Live frame** — the freshest image, pulled from the near-real-time feed (the others are the recent history).");
			sb.AppendLine("- **Freshness / latency (seconds)** — how OLD an image is = now minus when the radar actually scanned it. The live frame being ~60–120 s old is normal and good.");
			sb.AppendLine("- **Decode time (ms)** — how long it took to turn the raw radar bytes into on-screen pixels. Under ~1000 ms is fine.");
			sb.AppendLine("- **Sweep span (refl° / vel°)** — how much of the full 360° circle the scan covered. ~360 = a complete sweep; a low number means a partial / cut-off scan (a problem).");
			sb.AppendLine("- **tris / velTris** — triangle counts = roughly how much radar echo there is to draw (more storms = bigger numbers). Just a size gauge.");
			sb.AppendLine("- **Dealias** — the math that un-folds velocity (radar wraps fast winds around). The internal `hi` count being near 0 means it went cleanly.");
			sb.AppendLine("- **VCP / SAILS** — the radar's scan strategy. SAILS means it re-scans the lowest tilt extra times, so updates come faster.");
			sb.AppendLine("- **typical / near-worst (p50 / p95)** — p50 is the middle value (half were better); p95 is near the worst (only 5% were worse). A better summary than an average.");
			sb.AppendLine();

			// ── Run aggregates, each with a plain verdict + the raw numbers in [brackets]. ──
			sb.AppendLine("## Whole-run numbers");
			sb.AppendLine();
			sb.AppendLine($"- **Frames decoded:** {Stats.FramesDecoded}, of which **{Stats.SuspectCount} looked suspect** ({Pct(Stats.SuspectCount, Stats.FramesDecoded)}). {(Stats.SuspectCount == 0 ? "Clean." : "See Problems below.")}");
			sb.AppendLine($"- **Rendering:** {Stats.RenderErrors} graphics errors, {Stats.RenderBlanks} blank frames. {(problems ? "The radar may have flickered or briefly vanished." : "Rendering was clean.")}");
			sb.AppendLine($"- **Decode time:** {Plain(Stats.DecodeMs, 1000, 1600, "ms")} {Raw(Stats.DecodeMs)}");
			sb.AppendLine($"- **Live-frame freshness:** {Plain(Stats.NewestLatency, 120, 240, "s")} — how far behind real-time the live image runs. {Raw(Stats.NewestLatency)}");
			sb.AppendLine($"- **Whole-loop age:** {Plain(Stats.Latency, 3600, 5400, "s")} — includes the oldest history frames, so larger is expected. {Raw(Stats.Latency)}");
			sb.AppendLine($"- **Update interval:** {Plain(Stats.PollIntervals, 45, 70, "s")} — gap between live-frame checks (aiming ~30 s). {Raw(Stats.PollIntervals)}");
			sb.AppendLine();

			// ── The site you're on now. ──
			sb.AppendLine("## This session (the site you're on now)");
			sb.AppendLine();
			if (Live.Site is null)
			{
				sb.AppendLine("_No radar site selected right now._");
			}
			else
			{
				sb.AppendLine($"- **Site:** {Live.Site} (selection #{Live.Id})");
				sb.AppendLine($"- **Scan mode:** {Live.Mode ?? "—"}");
				sb.AppendLine($"- **How fast it loaded:** first image in {Fmt(Live.FirstFrameSec)} s, live image in {Fmt(Live.FirstLiveSec)} s, whole loop in {Fmt(Live.AllFramesSec)} s.");
				sb.AppendLine($"- **Frames loaded:** {Live.ReadyCount} of {Live.FrameCount}.");
				sb.AppendLine($"- **Live updates:** {Live.PollOkCount} of {Live.PollCount} succeeded{(pollFails > 0 ? $" ({pollFails} failed — likely network)" : "")}.");
				sb.AppendLine();
				sb.AppendLine("Each row below is one image in the loop. **Status** is the plain read; the rest are the raw numbers.");
				sb.AppendLine();
				sb.AppendLine("| # | type | scan time (UTC) | age (s) | status | refl° | vel° | decode ms |");
				sb.AppendLine("|--:|------|-----------------|--------:|--------|------:|-----:|----------:|");
				foreach (var kv in Live.Frames.OrderBy(k => k.Key))
				{
					var f = kv.Value;
					var volZ = f.VolumeTimeZ is { } vz ? vz.ToUniversalTime().ToString("HH:mm:ss") : "—";
					sb.AppendLine($"| {kv.Key} | {f.Source ?? "—"} | {volZ} | {Fmt(f.LatencySec)} | {FrameStatus(f)} | " +
						$"{f.ReflSpan:0} | {f.VelSpan:0} | {Fmt(f.DecodeMs)} |");
				}
			}
			sb.AppendLine();

			// ── Problems, in plain language. ──
			sb.AppendLine("## Problems & warnings");
			sb.AppendLine();
			if (!problems && !warnings)
			{
				sb.AppendLine("_None — everything looks healthy._");
			}
			else
			{
				if (Stats.RenderErrors > 0 || Stats.RenderBlanks > 0)
				{
					sb.AppendLine($"- **Rendering trouble:** {Stats.RenderErrors} graphics error(s), {Stats.RenderBlanks} blank frame(s) over the run — the radar layer may have flickered or briefly disappeared.");
				}
				if (pollFails > 0)
				{
					sb.AppendLine($"- **Live updates failing:** {pollFails} of {Live.PollCount} live checks didn't return data this session — usually a network blip.");
				}
				foreach (var a in Live.Anomalies.AsEnumerable().Reverse().Take(40))
				{
					sb.AppendLine($"- **{a.At:HH:mm:ss} — frame {a.Index} looked broken:** {PlainReason(a.Reason)}" +
						(a.Quarantined is { } q ? $" (raw file saved for inspection: `_suspect/{_stamp}/{q}`)" : ""));
				}
			}
			return sb.ToString();
		}

		// One-sentence plain-English summary for the headline.
		private static string BuildSummary(double? liveAge, int pollFails)
		{
			if (Live.Site is null)
			{
				return "No radar site is selected. Pick a site to start a loop.";
			}
			var parts = new List<string>
			{
				$"{Live.ReadyCount}/{Live.FrameCount} frames loaded for {Live.Site}",
			};
			if (liveAge is { } a) parts.Add($"live image ~{a:0} s old");
			parts.Add(Stats.RenderErrors + Stats.RenderBlanks == 0 ? "no render errors" : $"{Stats.RenderErrors + Stats.RenderBlanks} render issue(s)");
			parts.Add(Live.Anomalies.Count == 0 ? "no suspect frames this session" : $"{Live.Anomalies.Count} suspect frame(s) this session");
			if (pollFails > 0) parts.Add($"{pollFails} failed live update(s)");
			return string.Join(", ", parts) + ".";
		}

		// Plain status for one loop frame.
		private static string FrameStatus(FrameRec f) =>
			f.Suspect ? PlainReason(f.SuspectReason ?? "?")
			: f.HasData == false ? "empty"
			: f.Source == "live" ? "ok (live)"
			: "ok";

		// Turns a terse suspect reason into something readable.
		private static string PlainReason(string reason) => reason switch
		{
			"empty" => "no data in the scan",
			_ when reason.StartsWith("refl partial", StringComparison.Ordinal) => "reflectivity scan was cut off (partial sweep)",
			_ when reason.StartsWith("vel wedge", StringComparison.Ordinal) => "velocity scan was only a wedge (incomplete)",
			"vel zero gates" => "velocity had no data",
			"no refl gates" => "no reflectivity data",
			_ when reason.StartsWith("dealias hi", StringComparison.Ordinal) => "velocity un-folding looked off (too many out-of-range gates)",
			_ => reason,
		};

		// "typically <p50> <unit> (looks fine | a little high | high — worth a look)" from thresholds.
		private static string Plain(List<double> data, double goodP50, double concernP50, string unit)
		{
			if (data.Count == 0) return "no samples yet";
			var s = data.OrderBy(x => x).ToList();
			double p50 = s[Math.Clamp((int)(0.5 * (s.Count - 1)), 0, s.Count - 1)];
			string v = p50 <= goodP50 ? "looks fine" : p50 <= concernP50 ? "a little high" : "high — worth a look";
			return $"typically {p50:0} {unit} ({v})";
		}

		// The precise quantiles, in brackets, for exact reading (kept alongside the plain phrase).
		private static string Raw(List<double> data) => $"[{Quantiles(data)}]";

		private static string Pct(int n, int total) => total > 0 ? $"{100.0 * n / total:0.0}%" : "0%";

		// ── small helpers ──

		private static double Round(double v) => Math.Round(v, 2);
		private static string Fmt(double? v) => v is { } x ? x.ToString("0.0", CultureInfo.InvariantCulture) : "—";

		private static string Quantiles(List<double> data)
		{
			if (data.Count == 0) return "n=0";
			var s = data.OrderBy(x => x).ToList();
			double Q(double p) => s[Math.Clamp((int)(p * (s.Count - 1)), 0, s.Count - 1)];
			return $"n={s.Count} min={s[0]:0.0} p50={Q(0.5):0.0} p95={Q(0.95):0.0} max={s[^1]:0.0}";
		}

		private static int? GetInt(JsonElement e, string name) =>
			e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
		private static double? GetDouble(JsonElement e, string name) =>
			e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
		private static bool? GetBool(JsonElement e, string name) =>
			e.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : null;
		private static string? GetStr(JsonElement e, string name) =>
			e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

		// reflStats/velStats are nested objects { rad, azLo, azHi, span, gates }; pull the span/gates.
		private static double GetSpan(JsonElement e, string name) =>
			e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object && v.TryGetProperty("span", out var s) && s.ValueKind == JsonValueKind.Number
				? s.GetDouble() : 0;
		private static int GetStatGates(JsonElement e, string name) =>
			e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object && v.TryGetProperty("gates", out var g) && g.ValueKind == JsonValueKind.Number
				? g.GetInt32() : 0;

		// dealias string looks like "5reg seedMean.. vad../.. v[..,..] hi=12/3456" — pull hi=#/#.
		private static (int hi, int total) ParseDealiasHi(string? dealias)
		{
			if (string.IsNullOrEmpty(dealias)) return (0, 0);
			var i = dealias.IndexOf("hi=", StringComparison.Ordinal);
			if (i < 0) return (0, 0);
			var rest = dealias.Substring(i + 3);
			var slash = rest.IndexOf('/');
			if (slash < 0) return (0, 0);
			var hiStr = rest.Substring(0, slash);
			var totStr = new string(rest.Substring(slash + 1).TakeWhile(char.IsDigit).ToArray());
			int.TryParse(hiStr, out var hi);
			int.TryParse(totStr, out var total);
			return (hi, total);
		}

		// ── in-memory model ──

		private sealed class Session
		{
			public string? Site;
			public long Id;
			public DateTimeOffset? ClickAt;
			public DateTimeOffset? LastPollAt;
			public double? FirstFrameSec, AllFramesSec, FirstLiveSec;
			public int ReadyCount, FrameCount, PollCount, PollOkCount;
			public string? Mode;
			public readonly Dictionary<int, FrameRec> Frames = new();
			public readonly List<Anomaly> Anomalies = new();

			public FrameRec Frame(int idx)
			{
				if (!Frames.TryGetValue(idx, out var f)) { f = new FrameRec(); Frames[idx] = f; }
				return f;
			}

			public void Reset(string? site)
			{
				Site = site;
				ClickAt = null; LastPollAt = null;
				FirstFrameSec = AllFramesSec = FirstLiveSec = null;
				ReadyCount = FrameCount = PollCount = PollOkCount = 0;
				Mode = null;
				Frames.Clear();
				Anomalies.Clear();
			}
		}

		private sealed class FrameRec
		{
			public string? Source;
			public string? CacheFile;
			public DateTimeOffset? VolumeTimeZ;
			public double? LatencySec;
			public bool? HasData;
			public int Tris, VelTris;
			public double ReflSpan, VelSpan;
			public double? DecodeMs;
			public bool Suspect;
			public string? SuspectReason;
		}

		private sealed record Anomaly(DateTimeOffset At, int Index, string Reason, string? Quarantined);

		private sealed class RunStats
		{
			public long SessionCounter;
			public int FramesDecoded, SuspectCount, RenderErrors, RenderBlanks;
			public readonly List<double> DecodeMs = new();
			public readonly List<double> Latency = new();
			public readonly List<double> NewestLatency = new();
			public readonly List<double> PollIntervals = new();
		}
	}
}
