using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OfflineMapsTest.Models;
using OfflineMapsTest.Services;

namespace OfflineMapsTest.ViewModels
{
	// RadarViewModel (partial): the radar card presentation + next-update progress bar.
	public sealed partial class RadarViewModel
	{
		// ── Polished radar card (the user-facing presentation). The monospace RadarDebugText
		//    above feeds the collapsible "Diagnostics" expander; these are the headline values.
		//    All recomputed together via RaiseRadarReadout (frame changes + the 1s tick). ──

		/// <summary>Card header, e.g. "KVNX · Vance AFB".</summary>
		public string RadarCardTitle
		{
			get
			{
				var site = _selectedRadarOption?.Site;
				if (site is null) return string.Empty;
				return string.IsNullOrWhiteSpace(site.Name) ? site.Id : $"{site.Id} · {site.Name}";
			}
		}

		/// <summary>The selected site's ICAO id (e.g. "KTLX"), or empty when none.</summary>
		public string RadarSiteId => _selectedRadarOption?.Site?.Id ?? string.Empty;

		/// <summary>The selected site's descriptive name (e.g. "Norman"), or empty.</summary>
		public string RadarSiteName => _selectedRadarOption?.Site?.Name ?? string.Empty;

		/// <summary>Site coordinates for the card subheader.</summary>
		public string RadarCardCoords
		{
			get
			{
				var site = _selectedRadarOption?.Site;
				return site is null ? string.Empty : $"{site.Latitude:0.000}, {site.Longitude:0.000}";
			}
		}

		/// <summary>Headline: the displayed frame's local time, or load progress.</summary>
		public string RadarCardTime
		{
			get
			{
				if (_selectedRadarOption?.Site is null) return string.Empty;
				// Show the displayed frame's time as soon as IT is available (the newest frame
				// loads first, ~sub-second) rather than waiting for the whole loop to decode.
				var t = (_currentFrameIndex >= 0 && _currentFrameIndex < _frameTimes.Length) ? _frameTimes[_currentFrameIndex] : null;
				// Replay frames need the date — "5:00 PM" is ambiguous on a historical event; a live
				// loop is obviously today, so it stays time-only.
				if (t is { } x)
					return IsPastEventMode
						? x.ToLocalTime().ToString("h:mm tt · MMM d, yyyy")
						: x.ToLocalTime().ToString("h:mm tt");
				return string.Empty; // no "loading" text; the segmented scrubber shows load progress
			}
		}

		/// <summary>"frame 10/10 · live" under the headline time.</summary>
		public string RadarFrameDetail
		{
			get
			{
				if (_selectedRadarOption?.Site is null) return string.Empty;
				var hasTime = _currentFrameIndex >= 0 && _currentFrameIndex < _frameTimes.Length && _frameTimes[_currentFrameIndex] is not null;
				if (!hasTime) return string.Empty;
				var src = (_hasLiveFrame && _currentFrameIndex == _archiveCount) ? "live" : "archive";
				return $"frame {_currentFrameIndex + 1}/{_frameCount} · {src}";
			}
		}

		/// <summary>Backfill progress while older loop frames are still decoding in the background,
		/// e.g. "4/10 frames…" — the newest frame paints almost immediately, but the rest can
		/// take a few seconds, and without this the user has no sign more frames are still coming.
		/// The "Loading" label is the row header (see RadarControls.xaml), so it's not repeated here.
		/// Empty once the whole loop has decoded (<see cref="IsLoopReady"/>).</summary>
		public string RadarLoadingText =>
			(_frameCount > 0 && !_isLoopReady) ? $"{_readyCount}/{_frameCount} frames…" : string.Empty;

		/// <summary>Scan mode (VCP/precip/SAILS) from the latest live poll.</summary>
		public string RadarModeText
		{
			get
			{
				if (_selectedRadarOption?.Site is null) return string.Empty;
				// Replay has no live poll to fill _liveModeText, so read the DISPLAYED frame's own mode
				// (VCP + regime parsed from its cached tilt). Live loops keep the single poll value,
				// which carries the fuller "0.5°×N · SAILS" detail across all frames.
				if (IsPastEventMode)
				{
					var m = (_currentFrameIndex >= 0 && _currentFrameIndex < _frameModes.Length)
						? _frameModes[_currentFrameIndex] : null;
					return m ?? (_isLoopReady ? "—" : "loading…");
				}
				return _liveModeText ?? (_isLoopReady ? "—" : "loading…");
			}
		}

		// Velocity build progress, pushed from radar.js (radarBuildProgress). Velocity geometry is built
		// lazily (it's the one product that must dealias — ~1.5 s/frame on big super-res volumes), so on a
		// switch to Velocity the loop fills in over a few seconds. _velReady[i] = frame i's velocity is
		// built; only meaningful while Velocity is selected (refl/CC are always ready). It drives BOTH the
		// scrubber's per-cell fill (via RefreshSegmentReadiness) and playback's built-frontier hold, so the
		// build shows the same way as any other product's load — no separate textual readout.
		private bool[] _velReady = Array.Empty<bool>();

		/// <summary>Receives the velocity build state from the WebView. Refreshes the scrubber cells so the
		/// frames re-fill as their velocity dealiases; playback also reads it. <paramref name="ready"/> is
		/// per-frame (may be null).</summary>
		public void SetBuildProgress(int built, int total, bool[]? ready)
		{
			_velReady = ready ?? Array.Empty<bool>();
			RefreshSegmentReadiness();
		}

		// Whether frame idx is ready to display for the ACTIVE product. Velocity is built lazily, so a
		// not-yet-dealiased frame isn't ready; reflectivity/CC are always built. Missing/out-of-range
		// progress info returns true so playback never stalls on absent data.
		private bool IsFrameDisplayReady(int idx)
		{
			if (_radarProductIndex != 1) return true;      // reflectivity / CC are always built
			if (_velReady.Length == 0) return true;        // no progress pushed yet — don't stall
			if (idx < 0 || idx >= _velReady.Length) return true;
			return _velReady[idx];
		}

		/// <summary>How old the freshest available frame is, e.g. "2 min ago" / "1 hr 6 min ago".
		/// Uses the two largest non-zero units so a historical replay frame reads "13 yr 2 mo ago"
		/// instead of an unreadable ~7,000,000-minute count.</summary>
		public string RadarAgeText
		{
			get
			{
				if (NewestFrameTime() is not { } t) return "—";
				var span = DateTimeOffset.Now - t;
				return span.TotalMinutes < 1 ? "just now" : $"{FormatAge(span)} ago";
			}
		}

		// Renders a TimeSpan as its two largest non-zero units (yr/mo/day/hr/min). Month/year
		// lengths are averaged (30.44 / 365.25 days) — fine for a fuzzy "ago" readout.
		private static string FormatAge(TimeSpan span)
		{
			double totalDays = span.TotalDays;
			int years = (int)(totalDays / 365.25);
			double remDays = totalDays - years * 365.25;
			int months = (int)(remDays / 30.44);
			int days = (int)(remDays - months * 30.44);

			var parts = new (int Value, string Unit)[]
			{
				(years, "yr"), (months, "mo"), (days, "day"), (span.Hours, "hr"), (span.Minutes, "min"),
			};
			var shown = parts.SkipWhile(p => p.Value == 0).Take(2).Where(p => p.Value > 0);
			var text = string.Join(" ", shown.Select(p => $"{p.Value} {p.Unit}"));
			return text.Length == 0 ? "just now" : text;
		}

		/// <summary>Raw age of the freshest frame in minutes (null when no frame yet). Drives the
		/// smooth fresh→stale color ramp applied to the age readout — see RadarControls.AgeBrush.</summary>
		public double? RadarAgeMinutes =>
			NewestFrameTime() is { } t ? (DateTimeOffset.Now - t).TotalMinutes : null;

		/// <summary>Loop coverage, e.g. "12:36 – 1:12 PM · 10 frames".</summary>
		public string RadarLoopSpanText
		{
			get
			{
				if (_selectedRadarOption?.Site is null || _archiveCount == 0) return string.Empty;
				var oldest = _frameTimes.Length > 0 ? _frameTimes[0] : null;
				var newest = (_archiveCount - 1 < _frameTimes.Length) ? _frameTimes[_archiveCount - 1] : null;
				static string L(DateTimeOffset? x) => x?.ToLocalTime().ToString("h:mm tt") ?? "—";
				// Suffix the event date in replay so the time-only span isn't ambiguous on a past event.
				var date = IsPastEventMode && newest is { } n ? $" · {n.ToLocalTime():MMM d, yyyy}" : string.Empty;
				return $"{L(oldest)} – {L(newest)} · {_frameCount} frames{date}";
			}
		}

		/// <summary>Freshness of the newest frame, driving the status dot color.</summary>
		public RadarFreshness RadarStatus
		{
			get
			{
				// Based on the newest frame's age — available once that frame loads, no need to
				// wait for the full loop. None (gray) until then.
				if (NewestFrameTime() is not { } t) return RadarFreshness.None;
				var mins = (DateTimeOffset.Now - t).TotalMinutes;
				if (mins <= 12) return RadarFreshness.Live;
				return mins <= 30 ? RadarFreshness.Recent : RadarFreshness.Stale;
			}
		}

		// Freshest frame time available: the live slot if present, else the newest archive frame.
		private DateTimeOffset? NewestFrameTime()
		{
			if (_selectedRadarOption?.Site is null) return null;
			var live = (_hasLiveFrame && _archiveCount < _frameTimes.Length) ? _frameTimes[_archiveCount] : null;
			var arch = (_archiveCount > 0 && _archiveCount - 1 < _frameTimes.Length) ? _frameTimes[_archiveCount - 1] : null;
			if (live is { } l && arch is { } a) return l > a ? l : a;
			return live ?? arch;
		}

		// ── Next-update progress indicators (radar live frame). A 1s UI tick (RunProgressTickAsync)
		//    re-raises these so the bars fill smoothly. Math is shared in NextUpdate. ──

		/// <summary>Progress (0-100) toward the next live-frame poll, for the Selected Site bar.</summary>
		public double RadarNextFrameProgress => NextUpdate.ProgressOf(_livePollCycleStart, _nextLivePollAt);

		/// <summary>Countdown label to the next live-frame poll (e.g. "next ~12s").</summary>
		public string RadarNextFrameText => _selectedRadarOption?.Site is null ? "" : NextUpdate.CountdownOf(_nextLivePollAt);

		// Raises the polished card properties + the diagnostics text together (frame changes,
		// the 1s tick, selection changes). One call so notifications can't drift out of sync.
		private void RaiseRadarReadout()
		{
			OnPropertyChanged(nameof(RadarCardTitle));
			OnPropertyChanged(nameof(RadarSiteId));
			OnPropertyChanged(nameof(RadarSiteName));
			OnPropertyChanged(nameof(RadarCardCoords));
			OnPropertyChanged(nameof(RadarCardTime));
			OnPropertyChanged(nameof(RadarFrameDetail));
			OnPropertyChanged(nameof(RadarModeText));
			OnPropertyChanged(nameof(RadarAgeText));
			OnPropertyChanged(nameof(RadarAgeMinutes));
			OnPropertyChanged(nameof(RadarLoopSpanText));
			OnPropertyChanged(nameof(RadarStatus));
			OnPropertyChanged(nameof(RadarLoadingText));
		}

		/// <summary>Whether the loop is currently playing.</summary>
		public bool IsPlaying
		{
			get => _isPlaying;
			private set
			{
				if (_isPlaying == value)
				{
					return;
				}

				_isPlaying = value;
				OnPropertyChanged();
				OnPropertyChanged(nameof(PlayPauseGlyph));
			}
		}
	}
}
