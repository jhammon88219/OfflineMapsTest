using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Anvil.Models;
using Anvil.Services;

namespace Anvil.ViewModels
{
	/// <summary>
	/// DEV-ONLY automated site sweep ("automated manual test"). Walks every up site, activates each on
	/// the real live-loop path, dwells to let it fetch + decode a few frames, snapshots the diagnostics,
	/// and moves on — an unattended few-hour soak whose payoff is a triage list of which sites/VCPs
	/// produced suspect frames. It ADDS no decode or logging: the per-frame suspect heuristics +
	/// <c>_suspect/</c> quarantine already run in <see cref="RadarDiagnostics"/>; this only drives the
	/// selection and reads each site's tally back via <see cref="RadarDiagnostics.SnapshotSession"/>.
	///
	/// This is DISCOVERY, not regression: every run hits different live data. See docs/app-notes.md
	/// (Dev / testing) and docs/radar-validation.md (the fixed-corpus scorer that is the regression half).
	///
	/// Gated to Debug builds by the caller (the button lives behind <c>#if DEBUG</c> in Anvil.App).
	///
	/// Threading: <see cref="StartAsync"/> is invoked from the UI thread and never uses
	/// ConfigureAwait(false), so every await resumes on the UI thread — which is required, because the
	/// sweep drives <see cref="RadarViewModel.SelectedRadarOption"/> (a WinUI-bound property) and updates
	/// its own bound progress fields.
	/// </summary>
	public sealed class SiteSweepViewModel : INotifyPropertyChanged
	{
		private readonly RadarViewModel _radar;

		public SiteSweepViewModel(RadarViewModel radar)
		{
			_radar = radar;
			Results = new ObservableCollection<SweepSiteResult>();
		}

		// ── Parameters (bound to the params card) ──

		private int _dwellSeconds = 45;
		/// <summary>How long to let each site run before snapshotting and advancing. The main knob:
		/// ~160 sites × this ≈ total runtime.</summary>
		public int DwellSeconds
		{
			get => _dwellSeconds;
			set => SetField(ref _dwellSeconds, Math.Clamp(value, 5, 600));
		}

		private int _perSiteTimeoutSeconds = 90;
		/// <summary>Hard cap per site before the sweep gives up and advances regardless of dwell — the
		/// fault-isolation cutoff so one hung site can't stall an unattended run.</summary>
		public int PerSiteTimeoutSeconds
		{
			get => _perSiteTimeoutSeconds;
			set => SetField(ref _perSiteTimeoutSeconds, Math.Clamp(value, 10, 1200));
		}

		private int _framesPerSite;
		/// <summary>Advance early once this many frames have decoded, instead of waiting the full dwell
		/// (0 = always wait the full dwell). Whichever comes first.</summary>
		public int FramesPerSite
		{
			get => _framesPerSite;
			set => SetField(ref _framesPerSite, Math.Max(0, value));
		}

		private bool _skipOffline = true;
		/// <summary>Skip sites the status feed currently reports offline, so the sweep doesn't burn the
		/// per-site timeout on a site that is down or has no data.</summary>
		public bool SkipOffline
		{
			get => _skipOffline;
			set => SetField(ref _skipOffline, value);
		}

		private bool _operationalOnly = true;
		/// <summary>Restrict to operational WSR-88D sites (RadarSiteClass.Operational — excludes Research +
		/// TDWR). NOTE: "operational" is a CLASS, not a geography — it still includes the OCONUS operational
		/// sites (Alaska / Hawaii / Puerto Rico, and overseas ones like RKSG Camp Humphreys). Named for the
		/// class it filters on, not "CONUS", which the first sweep showed was misleading.</summary>
		public bool OperationalOnly
		{
			get => _operationalOnly;
			set => SetField(ref _operationalOnly, value);
		}

		// ── Live progress (bound to the params card while running) ──

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
		/// <summary>Human progress line: current site, N/total, elapsed, suspects so far.</summary>
		public string StatusText
		{
			get => _statusText;
			private set => SetField(ref _statusText, value);
		}

		/// <summary>Per-site results, appended live as the sweep runs (also the report's rows).</summary>
		public ObservableCollection<SweepSiteResult> Results { get; }

		private SweepReport? _lastReport;
		/// <summary>The finished run's report — set when a sweep completes or is stopped. The results
		/// window binds to this.</summary>
		public SweepReport? LastReport
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

		// The "[N/total] SITE Name" prefix for the current site, held so the per-second dwell countdown
		// can re-compose the status line without recomputing the index.
		private string _currentHeader = "";

		private CancellationTokenSource? _cts;

		/// <summary>Stop a running sweep. The in-flight site finishes its snapshot, then the report is
		/// built from whatever completed.</summary>
		public void Stop() => _cts?.Cancel();

		/// <summary>
		/// Run the sweep. Returns when every in-scope site has been visited (or the run is stopped).
		/// Each site is fully isolated: a throw or a timeout is recorded as that site's outcome and the
		/// sweep advances, so an unattended run survives any single site failing.
		/// </summary>
		public async Task StartAsync()
		{
			if (IsRunning)
			{
				return;
			}

			var sites = BuildSiteList();
			if (sites.Count == 0)
			{
				StatusText = "No sites match the current scope.";
				return;
			}

			_cts = new CancellationTokenSource();
			var ct = _cts.Token;
			IsRunning = true;
			Results.Clear();
			LastReport = null;

			var startedAt = DateTimeOffset.Now;
			var wasSelected = _radar.SelectedRadarOption; // restore the user's selection afterward

			RadarDiagnostics.Log("dev", "sweep.start", ("sites", sites.Count),
				("dwellSec", DwellSeconds), ("timeoutSec", PerSiteTimeoutSeconds),
				("framesPerSite", FramesPerSite), ("operationalOnly", OperationalOnly), ("skipOffline", SkipOffline));

			try
			{
				for (var i = 0; i < sites.Count && !ct.IsCancellationRequested; i++)
				{
					var option = sites[i];
					var site = option.Site!; // BuildSiteList only keeps options with a site
					_currentHeader = $"[{i + 1}/{sites.Count}] {site.Id} {site.Name}";
					StatusText = $"{_currentHeader} — loading… · {SuspectTally()}";

					var result = await SweepOneSiteAsync(site, option, ct);
					Results.Add(result);
				}
			}
			finally
			{
				// Best-effort restore: clear the sweep's last selection back to what the user had.
				try { _radar.SelectedRadarOption = wasSelected; } catch { /* ignore */ }

				var report = new SweepReport(startedAt, DateTimeOffset.Now, DwellSeconds, OperationalOnly,
					ct.IsCancellationRequested, Results.ToList());
				LastReport = report;
				IsRunning = false;
				StatusText = ct.IsCancellationRequested
					? $"Stopped after {Results.Count} sites — {SuspectTally()}."
					: $"Done. {Results.Count} sites swept — {SuspectTally()}.";

				RadarDiagnostics.Log("dev", "sweep.done", ("swept", Results.Count),
					("suspectSites", report.SuspectSiteCount), ("stopped", ct.IsCancellationRequested));

				_cts?.Dispose();
				_cts = null;
			}
		}

		// Runs one site with full fault isolation. Never throws — every failure becomes an outcome.
		private async Task<SweepSiteResult> SweepOneSiteAsync(RadarSite site, RadarOption option, CancellationToken runCt)
		{
			var t0 = DateTimeOffset.Now;
			try
			{
				using var siteCts = CancellationTokenSource.CreateLinkedTokenSource(runCt);
				siteCts.CancelAfter(TimeSpan.FromSeconds(PerSiteTimeoutSeconds));
				var ct = siteCts.Token;

				_radar.SelectedRadarOption = option; // fire-and-forget live load (see class remarks)

				await DwellAsync(ct);

				var snap = RadarDiagnostics.SnapshotSession();
				var elapsed = DateTimeOffset.Now - t0;

				// The snapshot is only trustworthy if the session is actually this site — a load that never
				// began (map not ready, immediate abort) leaves the previous site's session in place.
				var settled = string.Equals(snap.Site, site.Id, StringComparison.OrdinalIgnoreCase);
				if (!settled)
				{
					return new SweepSiteResult(site.Id, site.Name, SweepOutcome.NoData, 0, 0,
						Array.Empty<string>(), "session did not start for this site", elapsed);
				}

				var outcome = snap.SuspectCount > 0 ? SweepOutcome.Suspect
					: snap.ReadyCount > 0 ? SweepOutcome.Ok
					: SweepOutcome.NoData;

				return new SweepSiteResult(site.Id, site.Name, outcome, snap.ReadyCount, snap.SuspectCount,
					Distinct(snap.SuspectReasons), null, elapsed);
			}
			catch (OperationCanceledException) when (runCt.IsCancellationRequested)
			{
				// Whole run was stopped: record the in-flight site as skipped, don't swallow the stop.
				return new SweepSiteResult(site.Id, site.Name, SweepOutcome.Skipped, 0, 0,
					Array.Empty<string>(), "run stopped", DateTimeOffset.Now - t0);
			}
			catch (OperationCanceledException)
			{
				// Per-site timeout fired: still snapshot what we got before giving up.
				var snap = RadarDiagnostics.SnapshotSession();
				var settled = string.Equals(snap.Site, site.Id, StringComparison.OrdinalIgnoreCase);
				return new SweepSiteResult(site.Id, site.Name, SweepOutcome.Timeout,
					settled ? snap.ReadyCount : 0, settled ? snap.SuspectCount : 0,
					settled ? Distinct(snap.SuspectReasons) : Array.Empty<string>(),
					$"timed out after {PerSiteTimeoutSeconds}s", DateTimeOffset.Now - t0);
			}
			catch (Exception ex)
			{
				RadarDiagnostics.Log("dev", "sweep.site.error", ("site", site.Id), ("msg", ex.Message));
				return new SweepSiteResult(site.Id, site.Name, SweepOutcome.Error, 0, 0,
					Array.Empty<string>(), ex.Message, DateTimeOffset.Now - t0);
			}
		}

		// Waits up to DwellSeconds, ticking a live countdown into the status line each second and checking
		// for the FramesPerSite early-exit.
		private async Task DwellAsync(CancellationToken ct)
		{
			var deadline = DateTimeOffset.Now + TimeSpan.FromSeconds(DwellSeconds);
			ShowCountdown(deadline);
			while (DateTimeOffset.Now < deadline)
			{
				await Task.Delay(1000, ct);
				if (FramesPerSite > 0 && RadarDiagnostics.SnapshotSession().ReadyCount >= FramesPerSite)
				{
					return; // got enough frames, advance early
				}
				ShowCountdown(deadline);
			}
		}

		// "[N/total] SITE Name — 23s left · 0 suspect". Recomputed each dwell tick.
		private void ShowCountdown(DateTimeOffset deadline)
		{
			var remaining = Math.Max(0, (int)Math.Ceiling((deadline - DateTimeOffset.Now).TotalSeconds));
			StatusText = $"{_currentHeader} — {remaining}s left · {SuspectTally()}";
		}

		// Builds the in-scope, ordered option list from the radar VM's own site rows/options.
		private List<RadarOption> BuildSiteList()
		{
			var offline = _radar.RadarSiteRows
				.Where(r => r.IsOffline)
				.Select(r => r.Id)
				.ToHashSet(StringComparer.OrdinalIgnoreCase);

			return _radar.RadarOptions
				.Where(o => o.Site is not null)
				.Where(o => !OperationalOnly || o.Site!.Class == RadarSiteClass.Operational)
				.Where(o => !SkipOffline || !offline.Contains(o.Site!.Id))
				.OrderBy(o => o.Site!.Id, StringComparer.OrdinalIgnoreCase)
				.ToList();
		}

		private string SuspectTally()
		{
			var suspects = Results.Count(r => r.Outcome == SweepOutcome.Suspect);
			return suspects == 0 ? "0 suspect" : $"{suspects} suspect";
		}

		private static IReadOnlyList<string> Distinct(IReadOnlyList<string> reasons) =>
			reasons.Count == 0 ? Array.Empty<string>() : reasons.Distinct().ToList();

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

	/// <summary>How one site's sweep turned out.</summary>
	public enum SweepOutcome
	{
		/// <summary>Frames decoded, no suspects — the site is healthy.</summary>
		Ok,
		/// <summary>At least one frame tripped a suspect heuristic (the reason it's a triage target).</summary>
		Suspect,
		/// <summary>The site's session started but no frame ever decoded (down, no data, or nothing in range).</summary>
		NoData,
		/// <summary>The per-site timeout fired before the dwell finished.</summary>
		Timeout,
		/// <summary>An exception was thrown driving this site (isolated; the sweep continued).</summary>
		Error,
		/// <summary>The whole run was stopped while this site was in flight.</summary>
		Skipped,
	}

	/// <summary>One site's result within a sweep.</summary>
	public sealed record SweepSiteResult(
		string SiteId,
		string SiteName,
		SweepOutcome Outcome,
		int FramesDecoded,
		int SuspectCount,
		IReadOnlyList<string> SuspectReasons,
		string? Note,
		TimeSpan Elapsed)
	{
		/// <summary>One-line row for the results list / saved report.</summary>
		public string Display
		{
			get
			{
				var reasons = SuspectReasons.Count > 0 ? $" [{string.Join("; ", SuspectReasons)}]" : "";
				var note = Note is not null ? $" ({Note})" : "";
				return $"{SiteId,-5} {Outcome,-8} {FramesDecoded,3} frame(s){reasons}{note}";
			}
		}
	}

	/// <summary>The finished sweep: metadata + per-site results + a markdown serializer for saving.</summary>
	public sealed class SweepReport
	{
		public SweepReport(DateTimeOffset started, DateTimeOffset ended, int dwellSeconds,
			bool operationalOnly, bool stopped, IReadOnlyList<SweepSiteResult> results)
		{
			Started = started;
			Ended = ended;
			DwellSeconds = dwellSeconds;
			OperationalOnly = operationalOnly;
			Stopped = stopped;
			Results = results;
		}

		public DateTimeOffset Started { get; }
		public DateTimeOffset Ended { get; }
		public int DwellSeconds { get; }
		public bool OperationalOnly { get; }
		public bool Stopped { get; }
		public IReadOnlyList<SweepSiteResult> Results { get; }

		public TimeSpan Duration => Ended - Started;
		public int SweptCount => Results.Count;
		public int SuspectSiteCount => Results.Count(r => r.Outcome == SweepOutcome.Suspect);
		public int NoDataCount => Results.Count(r => r.Outcome == SweepOutcome.NoData);
		public int ErrorCount => Results.Count(r => r.Outcome is SweepOutcome.Error or SweepOutcome.Timeout);

		/// <summary>One-line headline for the results window.</summary>
		public string Summary =>
			$"{SweptCount} sites in {Duration:hh\\:mm\\:ss} — {SuspectSiteCount} suspect, " +
			$"{NoDataCount} no-data, {ErrorCount} error/timeout" + (Stopped ? " (stopped early)" : "");

		/// <summary>The saved-to-disk form: a self-contained markdown report, suspects first.</summary>
		public string ToMarkdown()
		{
			var sb = new StringBuilder();
			sb.Append("# Anvil site-sweep report\n\n");
			sb.Append($"- Started: {Started:yyyy-MM-dd HH:mm:ss}\n");
			sb.Append($"- Ended: {Ended:yyyy-MM-dd HH:mm:ss} ({Duration:hh\\:mm\\:ss})\n");
			sb.Append($"- Scope: {(OperationalOnly ? "operational only" : "all sites")}, dwell {DwellSeconds}s per site\n");
			sb.Append($"- {Summary}\n\n");

			void Section(string title, IEnumerable<SweepSiteResult> rows)
			{
				var list = rows.ToList();
				if (list.Count == 0) return;
				sb.Append($"## {title} ({list.Count})\n\n");
				foreach (var r in list)
				{
					sb.Append($"- `{r.SiteId}` {r.SiteName} — {r.FramesDecoded} frame(s)");
					if (r.SuspectReasons.Count > 0) sb.Append($" — {string.Join("; ", r.SuspectReasons)}");
					if (r.Note is not null) sb.Append($" — {r.Note}");
					sb.Append('\n');
				}
				sb.Append('\n');
			}

			// Suspects first — that's the triage list the whole run exists to produce.
			Section("Suspect", Results.Where(r => r.Outcome == SweepOutcome.Suspect));
			Section("Error / timeout", Results.Where(r => r.Outcome is SweepOutcome.Error or SweepOutcome.Timeout));
			Section("No data", Results.Where(r => r.Outcome == SweepOutcome.NoData));
			Section("OK", Results.Where(r => r.Outcome == SweepOutcome.Ok));
			Section("Skipped", Results.Where(r => r.Outcome == SweepOutcome.Skipped));
			return sb.ToString();
		}

		/// <summary>Suggested filename for the saved report.</summary>
		public string SuggestedFileName =>
			$"anvil-sweep-{Started:yyyyMMdd-HHmmss}.md";
	}
}
