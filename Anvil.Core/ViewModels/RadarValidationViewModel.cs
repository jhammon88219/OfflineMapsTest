using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Anvil.Models;
using Anvil.Services;

namespace Anvil.ViewModels
{
	/// <summary>
	/// DEV-ONLY velocity-dealias regression harness. Replays a FIXED corpus of committed <c>.V06</c>
	/// volumes (<see cref="IRadarCorpusProvider"/> / <c>Assets/radar-corpus.json</c>) through the REAL
	/// decode/dealias path in the WebView and scores each on its over-unfold ratio (hi/total, the
	/// <c>|v|&gt;55 m/s</c> gates the diagnostics call "dealias hi"), flagging any volume that got WORSE
	/// than its recorded baseline. Because the corpus is fixed and <c>dealiasSweepCore</c> is
	/// deterministic, the ONLY variable between runs is the dealias code — so this establishes a baseline
	/// (KBUF ≈ 31%) and guards a change from regressing it (the reverted VAD gap-fill that made KTLX 3×
	/// worse is the failure mode).
	///
	/// This is the REGRESSION half; the dev site sweep (<see cref="SiteSweepViewModel"/>) is the
	/// DISCOVERY half (live data, different every run). See docs/radar-validation.md.
	///
	/// Mirrors <see cref="SiteSweepViewModel"/>: a side VM constructed in Debug builds only, driving the
	/// WebView and reading results back — here by polling the JS <c>window.__anvilValidation</c> global
	/// (the async decode's Promise can't be awaited through ExecuteScriptAsync), the same "start then
	/// poll" shape the sweep uses against <c>RadarDiagnostics.SnapshotSession()</c>.
	///
	/// Threading: <see cref="StartAsync"/> is invoked from the UI thread and never uses
	/// ConfigureAwait(false), so awaits resume on the UI thread — required, as it updates bound props.
	/// </summary>
	public sealed class RadarValidationViewModel : INotifyPropertyChanged
	{
		private const int PollIntervalMs = 400;

		private readonly IMapService _map;
		private readonly IRadarCorpusProvider _corpus;

		public RadarValidationViewModel(IMapService map, IRadarCorpusProvider corpus)
		{
			_map = map;
			_corpus = corpus;
			Results = new ObservableCollection<ValidationResult>();
		}

		// ── Live state (bound to the run card) ──

		private bool _isRunning;
		public bool IsRunning
		{
			get => _isRunning;
			private set
			{
				if (SetField(ref _isRunning, value))
				{
					OnPropertyChanged(nameof(IsIdle));
				}
			}
		}
		public bool IsIdle => !_isRunning;

		private string _statusText = "Idle.";
		/// <summary>Human progress line: volumes done / total, worse count.</summary>
		public string StatusText
		{
			get => _statusText;
			private set => SetField(ref _statusText, value);
		}

		/// <summary>Per-volume results, rebuilt live from the WebView's progress as the run proceeds.</summary>
		public ObservableCollection<ValidationResult> Results { get; }

		private RadarValidationReport? _lastReport;
		/// <summary>The finished run's report — set on completion or stop. The results dialog binds to this.</summary>
		public RadarValidationReport? LastReport
		{
			get => _lastReport;
			private set
			{
				if (SetField(ref _lastReport, value))
				{
					OnPropertyChanged(nameof(HasReport));
				}
			}
		}
		public bool HasReport => _lastReport is not null;

		private CancellationTokenSource? _cts;

		/// <summary>Stop a running validation: signals the WebView to stop after the current volume, then
		/// builds the report from whatever completed.</summary>
		public void Stop()
		{
			_cts?.Cancel();
			_ = _map.CancelRadarValidationAsync();
		}

		/// <summary>
		/// Run the whole corpus once: start the WebView scorer, then poll its progress global until it
		/// finishes (or is stopped), rebuilding <see cref="Results"/> and finally the report.
		/// </summary>
		public async Task StartAsync()
		{
			if (IsRunning)
			{
				return;
			}

			var entries = _corpus.GetEntries();
			if (entries.Count == 0)
			{
				StatusText = "No corpus volumes found (Assets/radar-corpus.json).";
				return;
			}

			_cts = new CancellationTokenSource();
			var ct = _cts.Token;
			IsRunning = true;
			Results.Clear();
			LastReport = null;
			StatusText = $"Starting… {entries.Count} volume(s).";

			var startedAt = DateTimeOffset.Now;
			var byId = entries
				.GroupBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
				.ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

			try
			{
				var payload = entries.Select(e => new
				{
					id = e.Id,
					url = UrlFor(e),
					lat = e.Lat,
					lon = e.Lon,
				});
				await _map.StartRadarValidationAsync(JsonSerializer.Serialize(payload));

				// Poll the WebView progress until it reports finished. Each poll rebuilds Results in
				// manifest order (the corpus is tiny, so a full rebuild is cheaper than diffing).
				while (!ct.IsCancellationRequested)
				{
					await Task.Delay(PollIntervalMs, ct);

					var progress = ParseProgress(await _map.PollRadarValidationAsync());
					if (progress is null)
					{
						StatusText = "Waiting for the WebView…";
						continue;
					}

					RebuildResults(progress, byId);
					StatusText = $"[{progress.Done}/{progress.Total}] validating… · {WorseTally()}";

					if (progress.Finished)
					{
						break;
					}
				}
			}
			catch (OperationCanceledException)
			{
				// Stopped by the user — fall through and report what completed.
			}
			finally
			{
				var stopped = _cts?.IsCancellationRequested ?? false;
				var report = new RadarValidationReport(startedAt, DateTimeOffset.Now, Results.ToList(), stopped);
				LastReport = report;
				IsRunning = false;
				StatusText = stopped
					? $"Stopped — {report.Summary}"
					: $"Done — {report.Summary}";

				_cts?.Dispose();
				_cts = null;
			}
		}

		// The WebView fetches each committed volume from the radarcorpus host by filename.
		private static string UrlFor(RadarCorpusEntry e) =>
			$"https://{RadarCorpusProvider.CorpusHostName}/{e.File}";

		// Rebuild Results from the WebView progress, joining each decode result to its manifest baseline.
		private void RebuildResults(ValidationProgress progress, IReadOnlyDictionary<string, RadarCorpusEntry> byId)
		{
			Results.Clear();
			foreach (var r in progress.Results)
			{
				byId.TryGetValue(r.Id, out var entry);
				var actualPct = r.Ratio * 100.0;
				Results.Add(new ValidationResult(
					r.Id,
					entry?.Name ?? r.Id,
					actualPct,
					entry?.ExpectedPct ?? 0,
					entry?.TolerancePct ?? 0,
					r.GatesOver,
					r.GatesTotal,
					RadarValidationReport.Classify(entry, r.Error, actualPct),
					r.Error));
			}
		}

		private string WorseTally()
		{
			var worse = Results.Count(r => r.Status == ValidationStatus.Worse);
			return worse == 0 ? "0 worse" : $"{worse} WORSE";
		}

		// Parse the polled progress global, or null before it exists (poll returns the literal "null").
		private static ValidationProgress? ParseProgress(string? json)
		{
			if (string.IsNullOrWhiteSpace(json) || json.Trim() is "null" or "\"null\"")
			{
				return null;
			}
			try
			{
				return JsonSerializer.Deserialize<ValidationProgress>(json, ProgressJson);
			}
			catch
			{
				return null;
			}
		}

		private static readonly JsonSerializerOptions ProgressJson = new() { PropertyNameCaseInsensitive = true };

		// Shape of window.__anvilValidation (radar.js radarValidate). Plain class so a missing "results"
		// key deserializes to the empty default rather than null.
		private sealed class ValidationProgress
		{
			public int Total { get; set; }
			public int Done { get; set; }
			public bool Finished { get; set; }
			public List<ValidationRaw> Results { get; set; } = new();
		}

		private sealed class ValidationRaw
		{
			public string Id { get; set; } = "";
			public int GatesOver { get; set; }
			public int GatesTotal { get; set; }
			public double Ratio { get; set; }
			public string? Error { get; set; }
		}

		public event PropertyChangedEventHandler? PropertyChanged;
		private void OnPropertyChanged([CallerMemberName] string? name = null) =>
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
		private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
		{
			if (EqualityComparer<T>.Default.Equals(field, value)) return false;
			field = value;
			OnPropertyChanged(name);
			return true;
		}
	}

	/// <summary>How one corpus volume scored against its baseline.</summary>
	public enum ValidationStatus
	{
		/// <summary>Over-unfold ratio is within the baseline + tolerance — no regression.</summary>
		Pass,
		/// <summary>Over-unfold ratio exceeds baseline + tolerance — the dealias got WORSE on this volume.</summary>
		Worse,
		/// <summary>The volume decoded but has no manifest baseline to compare against.</summary>
		NoBaseline,
		/// <summary>The volume failed to decode / carried no velocity (couldn't be scored).</summary>
		Error,
	}

	/// <summary>One corpus volume's scored result within a validation run.</summary>
	public sealed record ValidationResult(
		string Id,
		string Name,
		double ActualPct,
		double ExpectedPct,
		double TolerancePct,
		int GatesOver,
		int GatesTotal,
		ValidationStatus Status,
		string? Error)
	{
		/// <summary>Percentage points the actual over-unfold is above (positive) or below the baseline.</summary>
		public double DeltaPct => ActualPct - ExpectedPct;

		/// <summary>One-line row for the results list / saved report.</summary>
		public string Display
		{
			get
			{
				if (Status == ValidationStatus.Error)
				{
					return $"{Id,-14} ERROR   {Error}";
				}
				var delta = DeltaPct >= 0 ? $"+{DeltaPct:F1}" : DeltaPct.ToString("F1", CultureInfo.InvariantCulture);
				var baseline = Status == ValidationStatus.NoBaseline
					? "(no baseline)"
					: $"vs {ExpectedPct:F1}% (Δ{delta})";
				return $"{Id,-14} {Status,-10} {ActualPct,5:F1}% {baseline}  [{GatesOver}/{GatesTotal}]";
			}
		}
	}

	/// <summary>A finished validation run: metadata + per-volume results + a markdown serializer, mirroring
	/// <see cref="SweepReport"/>. Holds the baseline-comparison logic (also unit-tested directly).</summary>
	public sealed class RadarValidationReport
	{
		public RadarValidationReport(DateTimeOffset started, DateTimeOffset ended,
			IReadOnlyList<ValidationResult> results, bool stopped)
		{
			Started = started;
			Ended = ended;
			Results = results;
			Stopped = stopped;
		}

		public DateTimeOffset Started { get; }
		public DateTimeOffset Ended { get; }
		public IReadOnlyList<ValidationResult> Results { get; }
		public bool Stopped { get; }

		public TimeSpan Duration => Ended - Started;
		public int VolumeCount => Results.Count;
		public int WorseCount => Results.Count(r => r.Status == ValidationStatus.Worse);
		public int PassCount => Results.Count(r => r.Status == ValidationStatus.Pass);
		public int ErrorCount => Results.Count(r => r.Status is ValidationStatus.Error or ValidationStatus.NoBaseline);

		/// <summary>The KTLX-3×-regression guard: a volume "got worse" when its over-unfold exceeds the
		/// recorded baseline by more than the allowed margin. No baseline ⇒ NoBaseline; a decode failure ⇒
		/// Error; otherwise Pass. Static + pure so it can be unit-tested without a WebView.</summary>
		public static ValidationStatus Classify(RadarCorpusEntry? entry, string? error, double actualPct)
		{
			if (entry is null) return ValidationStatus.NoBaseline;
			if (!string.IsNullOrEmpty(error)) return ValidationStatus.Error;
			return actualPct > entry.ExpectedPct + entry.TolerancePct
				? ValidationStatus.Worse
				: ValidationStatus.Pass;
		}

		/// <summary>One-line headline for the run.</summary>
		public string Summary =>
			$"{VolumeCount} volume(s) in {Duration:hh\\:mm\\:ss} — " +
			$"{WorseCount} worse, {PassCount} pass, {ErrorCount} error/no-baseline" +
			(Stopped ? " (stopped early)" : "");

		/// <summary>The saved-to-disk form: a self-contained markdown report, WORSE volumes first.</summary>
		public string ToMarkdown()
		{
			var sb = new StringBuilder();
			sb.Append("# Anvil velocity-dealias validation report\n\n");
			sb.Append($"- Started: {Started:yyyy-MM-dd HH:mm:ss}\n");
			sb.Append($"- Ended: {Ended:yyyy-MM-dd HH:mm:ss} ({Duration:hh\\:mm\\:ss})\n");
			sb.Append($"- {Summary}\n");
			sb.Append("- Metric: over-unfold ratio = gates |v|>55 m/s ÷ total velocity gates. " +
				"A volume is **Worse** when it exceeds its baseline + tolerance.\n\n");

			void Section(string title, IEnumerable<ValidationResult> rows)
			{
				var list = rows.ToList();
				if (list.Count == 0) return;
				sb.Append($"## {title} ({list.Count})\n\n");
				sb.Append("| Volume | Actual % | Baseline % | Δ pp | Gates over/total |\n");
				sb.Append("| --- | ---: | ---: | ---: | --- |\n");
				foreach (var r in list)
				{
					if (r.Status == ValidationStatus.Error)
					{
						sb.Append($"| `{r.Id}` {r.Name} | — | — | — | error: {r.Error} |\n");
						continue;
					}
					var baseline = r.Status == ValidationStatus.NoBaseline ? "—" : r.ExpectedPct.ToString("F1", CultureInfo.InvariantCulture);
					var delta = r.Status == ValidationStatus.NoBaseline
						? "—"
						: (r.DeltaPct >= 0 ? "+" : "") + r.DeltaPct.ToString("F1", CultureInfo.InvariantCulture);
					sb.Append($"| `{r.Id}` {r.Name} | {r.ActualPct.ToString("F1", CultureInfo.InvariantCulture)} | " +
						$"{baseline} | {delta} | {r.GatesOver}/{r.GatesTotal} |\n");
				}
				sb.Append('\n');
			}

			// Worse first — the whole run exists to surface regressions.
			Section("Worse (regressions)", Results.Where(r => r.Status == ValidationStatus.Worse));
			Section("Pass", Results.Where(r => r.Status == ValidationStatus.Pass));
			Section("No baseline", Results.Where(r => r.Status == ValidationStatus.NoBaseline));
			Section("Error", Results.Where(r => r.Status == ValidationStatus.Error));
			return sb.ToString();
		}

		/// <summary>Suggested filename for the saved report.</summary>
		public string SuggestedFileName =>
			$"anvil-dealias-validation-{Started:yyyyMMdd-HHmmss}.md";
	}
}
