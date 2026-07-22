using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Anvil.Models;
using Anvil.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>Freshness of a radar site's newest frame, driving the card's status dot.</summary>
	public enum RadarFreshness
	{
		/// <summary>No loop / not yet ready.</summary>
		None,
		/// <summary>Newest frame within ~12 min — current.</summary>
		Live,
		/// <summary>~12–30 min old.</summary>
		Recent,
		/// <summary>Over ~30 min old — the site looks stale/offline.</summary>
		Stale
	}

	/// <summary>
	/// View model for the radar subsystem: site selection + on-map markers, the animation loop
	/// (live + past-event replay), the near-real-time live frame, DOW frames, the radar card,
	/// color-scale legend, and the inspector. Extracted from MapViewModel; the transport-bar
	/// section controls bind slices of this. Drives the map through <see cref="IMapService"/>.
	/// </summary>
	public sealed partial class RadarViewModel : ObservableObject
	{
		private readonly IMapService _mapService;
		private readonly IRadarSiteProvider _radarSiteProvider;
		private readonly ILevel2RadarService _radarService;

		// Readiness guard: radar commands only run once the map page has reported 'mapReady'
		// (set by OnMapsReadyAsync, called from MapViewModel.OnMapsReadyAsync).
		private bool _isMapReady;

		// Selected radar site option ("None" clears the layer) + radar layer opacity.
		private RadarOption? _selectedRadarOption;
		private double _radarOpacity = 0.85;

		// Whether the on-map radar site marker buttons are shown. Independent of the radar
		// layer: hiding the markers leaves any active loop rendering.
		private bool _radarSitesVisible = true;
		private bool _showResearchRadars;   // research/test radars (KCRI) hidden until opted in
		private bool _showTdwrs;            // Terminal Doppler Weather Radars (T***) hidden until opted in

		// Radar loop state. The loop is a sequence of recent volumes (newest last).
		// _frameTimes[i] is set as each frame's volume caches; _readyCount tracks how many
		// have decoded in the WebView; _loadedNewestKey detects a new volume on refresh.
		// _loopCts cancels the load + auto-refresh + playback for the current selection.
		// Loop length (frame count) — user-selectable in the Radar Loop tool window via discrete
		// presets (so each change rebuilds the loop at most once). Capped at 30: memory + initial
		// decode cost grow with length, though steady-state stays cheap via the incremental reload.
		private static readonly int[] LoopLengthByIndex = { 6, 10, 15, 20, 25, 30 };
		private int _loopLengthIndex = 1; // default 10 frames
		private int LoopLength => LoopLengthByIndex[Math.Clamp(_loopLengthIndex, 0, LoopLengthByIndex.Length - 1)];
		private DateTimeOffset?[] _frameTimes = Array.Empty<DateTimeOffset?>();
		// Per-frame scan mode ("VCP 212 · precip"), parallel to _frameTimes. Populated for every
		// frame (archive via EnsureCachedAsync, live via the poll) but only SHOWN in replay, where
		// there's no live poll to drive the single _liveModeText. null = unknown for that frame.
		private string?[] _frameModes = Array.Empty<string?>();
		private int _frameCount;
		private int _readyCount;
		private int _currentFrameIndex;

		/// <summary>One cell per loop frame for the segmented scrubber; each <see cref="RadarFrameSegment.IsReady"/>
		/// flips true as that frame decodes (see <see cref="OnRadarFrameReady"/>). Rebuilt on every (re)load
		/// via <see cref="RebuildSegments"/>. The scrubber cells + playhead render from this.</summary>
		public System.Collections.ObjectModel.ObservableCollection<RadarFrameSegment> Segments { get; } = new();

		// Rebuilds Segments to `count` cells (a new loop / a remap). `readyFrom` optionally carries the
		// DECODE state to seed each new index (used by the incremental remap to keep reused frames lit);
		// null seeds all not-decoded. Each cell's displayed readiness is then derived for the active
		// product. Runs on the UI thread (load/remap resume there).
		private void RebuildSegments(int count, bool[]? readyFrom = null)
		{
			Segments.Clear();
			for (var i = 0; i < count; i++)
			{
				Segments.Add(new RadarFrameSegment { IsDecoded = readyFrom is { } r && i < r.Length && r[i] });
			}
			RefreshSegmentReadiness();
		}

		// Recomputes every scrubber cell's DISPLAYED readiness (RadarFrameSegment.IsReady) from its durable
		// decode state and the active product: Reflectivity/CC are ready as soon as decoded; Velocity also
		// needs its (lazily-built) dealiased geometry, so a decoded-but-not-yet-dealiased frame reads as
		// still-loading — this is what makes the scrubber FILL IN while velocity builds, exactly like the
		// initial decode. Called whenever decode state, velocity-build state, or the product changes.
		private void RefreshSegmentReadiness()
		{
			for (var i = 0; i < Segments.Count; i++)
			{
				Segments[i].IsReady = Segments[i].IsDecoded && IsFrameDisplayReady(i);
			}
		}
		private bool _isPlaying;
		private bool _isLoopReady;
		private string? _loadedNewestKey;
		// The ordered archive keys currently loaded (parallel to archive frame indices 0.._archiveCount-1).
		// Lets a periodic refresh diff old vs new keys and reuse the unchanged decoded frames instead
		// of rebuilding the whole loop (which blanked the layer + re-decoded everything).
		private string[] _loadedKeys = Array.Empty<string>();
		private CancellationTokenSource? _loopCts;

		// ── Past Event Viewer (replay a historical window instead of the live loop) ──
		// Start of the decodable archive. 2008+ is Message-31 super-res; 1991-2007 is legacy AR2V0001
		// Message 1 (single-pol, no CC), now decoded by the vendored Message-1 path. The WSR-88D
		// network came online ~1991-1997 (KTLX's earliest archived volumes are ~1993); empty days for
		// a site simply list nothing.
		private const int PastEventStartYear = 1991;
		private bool _isPastEventMode;
		// Default to 2011-05-24 (a frequently-revisited event). Indices are into the option lists:
		// year 1991-based, month 0-based, day 0-based.
		private int _pastEventYearIndex = 2011 - PastEventStartYear;
		private int _pastEventMonthIndex = 5 - 1;
		private int _pastEventDayIndex = 24 - 1;
		private TimeSpan _pastEventTime = new(17, 0, 0); // default 5:00 PM
		private int _pastEventDurationIndex = 2; // default 2 hours (index into PastEventDurationOptions)
		private string _pastEventStatus = string.Empty;

		// Serializes loop mutation so the archive (re)load and the live-frame poll can't
		// interleave at await points and corrupt the frame arrays / VM↔JS index state.
		private readonly System.Threading.SemaphoreSlim _loopGate = new(1, 1);

		// Near-real-time "live" frame from the chunks bucket, appended as an extra newest frame
		// (index _archiveCount) on top of the archive-bucket history. _hasLiveFrame says whether
		// that slot exists; _liveFrame holds the current one (its time gates in-place updates).
		// A faster poll (RunLiveFrameRefreshAsync) keeps it fresh between archive reloads; 30s
		// catches each new SAILS 0.5° re-scan (~every 1.5-3 min) soon after it finishes without
		// much wasted traffic (clear-air VCPs only scan ~every 10 min, the real floor there).
		// Live-frame poll cadence — user-selectable in the Radar Loop tool window. RunLiveFrameRefreshAsync
		// reads it each cycle, so a change takes effect on the next poll with no reload. (The faster
		// retry-until-first-frame cadence below is unaffected.)
		private static readonly double[] RefreshSecondsByIndex = { 20, 30, 45, 60 };
		private int _refreshIntervalIndex = 1; // default 30 s
		private double RefreshIntervalSeconds => RefreshSecondsByIndex[Math.Clamp(_refreshIntervalIndex, 0, RefreshSecondsByIndex.Length - 1)];
		// Playback animation speed — user-selectable. RunPlaybackAsync reads ms-per-frame each tick,
		// so a change applies immediately (no restart).
		private static readonly int[] PlaybackMsByIndex = { 1000, 500, 333, 250 }; // 0.5x / 1x / 1.5x / 2x
		private int _playbackSpeedIndex = 1; // default 1x (500 ms)
		private int PlaybackIntervalMs => PlaybackMsByIndex[Math.Clamp(_playbackSpeedIndex, 0, PlaybackMsByIndex.Length - 1)];
		// While no live frame exists yet (the load-time poll often hits a still-scanning volume),
		// retry faster so the live data appears sooner; back to the normal cadence once we have one.
		private const double LiveFrameRetrySeconds = 20;
		private int _archiveCount;
		private bool _hasLiveFrame;
		private Models.RadarVolume? _liveFrame;
		// Mode text (VCP/precip/SAILS) from the most recent successful live poll. Tracked
		// SEPARATELY from _liveFrame because the mode is known from any decoded live volume even
		// when we don't append it as a new frame — e.g. an offline/stale site (KVNX) whose newest
		// chunks volume merely equals the archive newest, so it's correctly not appended, yet we
		// still want to show its scan mode rather than "awaiting live frame" forever.
		private string? _liveModeText;

		// Debug card state: outcome of the most recent live-frame poll (for the on-map dev card).
		private DateTimeOffset? _lastLivePollAt;
		private string? _lastLivePollResult;
		private string? _lastLiveError;
		// When the next live-frame poll is scheduled (set by RunLiveFrameRefreshAsync before each
		// wait), so the card can show a countdown / progress to it. _livePollCycleStart is the start
		// of the current wait (the progress-bar denominator).
		private DateTimeOffset? _nextLivePollAt;
		private DateTimeOffset? _livePollCycleStart;


		// Load timing for the current selection: from the site click to the first frame ready,
		// and to ALL frames (final count, incl. the live frame) ready+rendered. Captured once per
		// click (frozen after the initial load, so the ~60s live refreshes don't overwrite them).
		private DateTimeOffset? _loopClickAt;
		private TimeSpan? _firstFrameElapsed;
		private TimeSpan? _allFramesElapsed;
		private bool _initialLoadDone;
		private bool _loadInProgress;
		// Set true once BeginRadarLoopAsync has (re)started the loop for the current selection; gates
		// OnRadarFrameReady so stale frames from the previous selection (arriving before the JS loop
		// token bumps) can't pollute this session's first-frame timing / ready count.
		private bool _loopRenderBegun;

		public RadarViewModel(IMapService mapService, IRadarSiteProvider radarSiteProvider, ILevel2RadarService radarService, IDowEventProvider dowEventProvider)
		{
			_mapService = mapService;
			_radarSiteProvider = radarSiteProvider;
			_radarService = radarService;

			// The DOW Event Viewer is its own view model (a standalone mobile-radar frame through the
			// same render path); watch its IsShowing so the shared display / color-scale gate follows it.
			Dow = new DowViewModel(mapService, dowEventProvider);
			Dow.PropertyChanged += (_, e) =>
			{
				if (e.PropertyName == nameof(DowViewModel.IsShowing))
				{
					OnPropertyChanged(nameof(HasRadarDisplay));
					OnPropertyChanged(nameof(HasColorScale));
				}
			};

			// Radar site selector: a leading "None" entry plus the curated sites.
			var radarOptions = new List<RadarOption> { new("None", null) };
			radarOptions.AddRange(_radarSiteProvider.GetSites().Select(s => new RadarOption($"{s.Id} \u2014 {s.Name}", s)));
			RadarOptions = radarOptions;
			_selectedRadarOption = RadarOptions[0];

			// Observable rows for the dock's "Radar Sites" list (site + offline state).
			var rows = _radarSiteProvider.GetSites().Select(s => new RadarSiteRow(s)).ToList();
			RadarSiteRows = rows;
			_rowBySite = rows.ToDictionary(r => r.Site);
		}

		/// <summary>Observable rows (site + offline state) for the dock's "Radar Sites" list.</summary>
		public IReadOnlyList<RadarSiteRow> RadarSiteRows { get; }

		private readonly IReadOnlyDictionary<RadarSite, RadarSiteRow> _rowBySite;

		/// <summary>Radar site options: a leading "None" entry plus the curated sites.</summary>
		public IReadOnlyList<RadarOption> RadarOptions { get; }

		/// <summary>
		/// The selected radar option. Setting a site recenters the map on it and loads a loop
		/// of recent Level II volumes (newest shown first, older backfilled); "None" clears the
		/// radar and stops the loop.
		/// </summary>
		public RadarOption? SelectedRadarOption
		{
			get => _selectedRadarOption;
			set
			{
				if (!SetProperty(ref _selectedRadarOption, value))
				{
					return;
				}

				// Mirror the selection to the dock list (so a map-marker pick highlights its row).
				// Guarded so the list's own setter doesn't bounce back into a re-select.
				_syncingSelection = true;
				SelectedSiteRow = value?.Site is { } site && _rowBySite.TryGetValue(site, out var row) ? row : null;
				_syncingSelection = false;
				Dow.OnNexradTookOver(); // a NEXRAD selection takes over the radar layer from any DOW frame
				OnPropertyChanged(nameof(HasRadarLoop));
				OnPropertyChanged(nameof(HasRadarDisplay));
				OnPropertyChanged(nameof(HasColorScale));
				RaiseRadarReadout();
				// Past Event mode: before a window is armed, a site pick just targets it (Load drives the
				// first replay); once armed, a site pick auto-loads the same window for the new site (like
				// the live loop's click-to-load). Live mode always starts the live loop on a pick.
				if (_isPastEventMode)
				{
					if (_pastWindowLoaded && value?.Site is not null)
					{
						_ = LoadSelectedPastEventAsync();
					}
					else
					{
						_ = SelectPastSiteAsync(value?.Site);
					}
				}
				else
				{
					_ = StartRadarLoopAsync(value?.Site);
				}
			}
		}

		/// <summary>Whether a radar site is selected (drives the loop controls' visibility).</summary>
		public bool HasRadarLoop => _selectedRadarOption?.Site is not null;

		/// <summary>Whether ANY radar frame is currently displayed — a NEXRAD loop OR a DOW frame. Gates
		/// the product Inspect toggle and the Color Scale legend, which apply to both sources.</summary>
		public bool HasRadarDisplay => HasRadarLoop || Dow.IsShowing;

		/// <summary>The DOW Event Viewer view model (a standalone curated mobile-radar frame).</summary>
		public DowViewModel Dow { get; }

		// ── Past Event Viewer ────────────────────────────────────────────────────────────────────
		// A second radar "mode": instead of the live loop (recent volumes + a near-real-time frame
		// that auto-refreshes), replay a fixed historical window the user picks. Same loop machinery
		// (decode/render/scrub/play), but no live poll and no auto-refresh. The site is picked in the
		// normal Radar Sites list; this just supplies the time window + a Load action.

		/// <summary>Durations offered for a past-event window (label + minutes).</summary>
		public IReadOnlyList<string> PastEventDurationOptions { get; } =
			new[] { "30 min", "1 hour", "2 hours", "3 hours", "6 hours", "12 hours" };
		private static readonly int[] PastEventMinutesByIndex = { 30, 60, 120, 180, 360, 720 };
		// Cap on frames loaded. Short windows load every volume (~5 min apart, smooth); longer windows
		// are evenly SUBSAMPLED to this many frames (so a 12 h window is an overview, ~18 min apart,
		// rather than 140+ frames melting memory).
		private const int PastEventMaxFrames = 40;

		/// <summary>Year choices for the date picker (1991 = start of the decodable WSR-88D archive).</summary>
		public IReadOnlyList<int> PastEventYearOptions { get; } =
			Enumerable.Range(PastEventStartYear, DateTime.Now.Year - PastEventStartYear + 1).ToList();

		/// <summary>Month choices (1-12).</summary>
		public IReadOnlyList<int> PastEventMonthOptions { get; } = Enumerable.Range(1, 12).ToList();

		/// <summary>Day choices (1-31; an out-of-range day for the month is clamped on Load).</summary>
		public IReadOnlyList<int> PastEventDayOptions { get; } = Enumerable.Range(1, 31).ToList();

		/// <summary>Selected year index (into <see cref="PastEventYearOptions"/>).</summary>
		public int PastEventYearIndex
		{
			get => _pastEventYearIndex;
			set { var c = Math.Clamp(value, 0, PastEventYearOptions.Count - 1); SetProperty(ref _pastEventYearIndex, c); }
		}

		/// <summary>Selected month index (0-11).</summary>
		public int PastEventMonthIndex
		{
			get => _pastEventMonthIndex;
			set { var c = Math.Clamp(value, 0, 11); SetProperty(ref _pastEventMonthIndex, c); }
		}

		/// <summary>Selected day index (0-30).</summary>
		public int PastEventDayIndex
		{
			get => _pastEventDayIndex;
			set { var c = Math.Clamp(value, 0, 30); SetProperty(ref _pastEventDayIndex, c); }
		}

		// Once a replay window has been loaded (Load pressed with data found), the window is "armed":
		// after that, picking another site auto-loads the SAME window for it (like the live loop's
		// click-to-load), instead of forcing the user back into the flyout. Reset on mode change.
		private bool _pastWindowLoaded;

		/// <summary>
		/// When true, the app is in historical-replay mode: live controls gray out, a site pick targets
		/// the Load action (until a window is armed, then it auto-loads), and toggling off clears to idle.
		/// </summary>
		public bool IsPastEventMode
		{
			get => _isPastEventMode;
			set
			{
				if (!SetProperty(ref _isPastEventMode, value))
				{
					return;
				}

				_pastWindowLoaded = false; // re-arm from scratch each time the mode is toggled
				OnPropertyChanged(nameof(IsLiveControlsEnabled));
				// The offered tilts depend on the mode, not just the radar: a live loop shows only the
				// tilts the chunks feed can serve fresh, while replay (all-historical) offers the whole
				// VCP. Rebuild from the last-known VCP now rather than waiting for a frame to land.
				UpdateTiltOptions(null);
				// Both directions clear to a clean slate (entering: drop the live loop; leaving:
				// drop the replay loop and go idle). Setting "None" routes through the mode-aware
				// SelectedRadarOption setter, which clears the loop without starting anything.
				SelectedRadarOption = RadarOptions[0];
				PastEventStatus = value ? "Pick a site, set a start time, then Load." : string.Empty;
				// Leaving replay: restore the LIVE site availability promptly (the status loop skips its
				// pushes while in past mode, so it wouldn't refresh the markers for up to ~10 min).
				if (!value)
				{
					_ = RefreshLiveSiteStatusAsync();
				}
			}
		}

		/// <summary>Inverse of <see cref="IsPastEventMode"/>; bound to IsEnabled on the live-only controls.</summary>
		public bool IsLiveControlsEnabled => !_isPastEventMode;

		/// <summary>Start time-of-day of the replay window (bound to a TimePicker, local time).</summary>
		public TimeSpan PastEventTime
		{
			get => _pastEventTime;
			set => SetProperty(ref _pastEventTime, value);
		}

		/// <summary>The replay window's UTC start, reconstructed from the Year/Month/Day/time controls
		/// (local midnight for the selected date + the start time-of-day, then to UTC; the day is clamped to
		/// the month's length). This VM owns the replay-date state, so overlays keyed to the replay date (the
		/// historical outlook, storm reports) read the instant from here rather than each re-deriving it.
		/// <see cref="LoadSelectedPastEventAsync"/> keeps its own inline copy because it also needs the end.</summary>
		internal DateTimeOffset ReplayStartUtc()
		{
			var year = PastEventYearOptions[_pastEventYearIndex];
			var month = _pastEventMonthIndex + 1;
			var day = Math.Min(_pastEventDayIndex + 1, DateTime.DaysInMonth(year, month));
			var localMidnight = new DateTimeOffset(year, month, day, 0, 0, 0,
				TimeZoneInfo.Local.GetUtcOffset(new DateTime(year, month, day)));
			return (localMidnight + _pastEventTime).ToUniversalTime();
		}

		/// <summary>Selected window-duration index (into <see cref="PastEventDurationOptions"/>).</summary>
		public int PastEventDurationIndex
		{
			get => _pastEventDurationIndex;
			set
			{
				var clamped = Math.Clamp(value, 0, PastEventMinutesByIndex.Length - 1);
				SetProperty(ref _pastEventDurationIndex, clamped);
			}
		}

		/// <summary>Status line for the Past Event Viewer (loading / loaded N frames / errors).</summary>
		public string PastEventStatus
		{
			get => _pastEventStatus;
			private set => SetProperty(ref _pastEventStatus, value);
		}

		// ── User-tunable loop settings (Radar Loop tool window). SelectedIndex-bound combos,
		//    matching the existing Product combo pattern. ──

		/// <summary>Loop-length presets (frame counts) for the combo.</summary>
		public IReadOnlyList<int> LoopLengthOptions { get; } = new[] { 6, 10, 15, 20, 25, 30 };

		/// <summary>Selected loop-length index; changing it rebuilds the loop at the new length.</summary>
		public int LoopLengthIndex
		{
			get => _loopLengthIndex;
			set
			{
				var clamped = Math.Clamp(value, 0, LoopLengthByIndex.Length - 1);
				if (SetProperty(ref _loopLengthIndex, clamped) && _selectedRadarOption?.Site is { } site)
				{
					_ = StartRadarLoopAsync(site);
				}
			}
		}

		/// <summary>Update-interval presets (live-frame poll cadence) for the combo.</summary>
		public IReadOnlyList<string> RefreshIntervalOptions { get; } = new[] { "20 s", "30 s", "45 s", "60 s" };

		/// <summary>Selected update-interval index; applied on the next live poll (no reload).</summary>
		public int RefreshIntervalIndex
		{
			get => _refreshIntervalIndex;
			set
			{
				var clamped = Math.Clamp(value, 0, RefreshSecondsByIndex.Length - 1);
				SetProperty(ref _refreshIntervalIndex, clamped);
			}
		}

		/// <summary>Playback-speed presets for the combo.</summary>
		public IReadOnlyList<string> PlaybackSpeedOptions { get; } = new[] { "0.5×", "1×", "1.5×", "2×" };

		/// <summary>Selected playback-speed index; applied on the next animation tick.</summary>
		public int PlaybackSpeedIndex
		{
			get => _playbackSpeedIndex;
			set
			{
				var clamped = Math.Clamp(value, 0, PlaybackMsByIndex.Length - 1);
				SetProperty(ref _playbackSpeedIndex, clamped);
			}
		}

		private RadarSiteRow? _selectedSiteRow;
		private bool _syncingSelection;

		/// <summary>
		/// The row selected in the dock's "Radar Sites" list. Two-way bound to the ListView's
		/// SelectedItem so a map-marker pick highlights the matching row and a list pick activates the
		/// site — both funnel through the single <see cref="SelectedRadarOption"/> source of truth.
		/// </summary>
		public RadarSiteRow? SelectedSiteRow
		{
			get => _selectedSiteRow;
			set
			{
				if (!SetProperty(ref _selectedSiteRow, value)) return;
				if (_syncingSelection) return; // pushed from SelectedRadarOption — don't re-activate
				// The list drove this: select the matching option (a plain select, not a toggle).
				SelectedRadarOption = value is null
					? RadarOptions[0]
					: RadarOptions.FirstOrDefault(o => o.Site is { } s && s == value.Site) ?? RadarOptions[0];
			}
		}

		/// <summary>Whether all loop frames have finished decoding (enables play + scrubber).</summary>
		public bool IsLoopReady
		{
			get => _isLoopReady;
			private set
			{
				if (!SetProperty(ref _isLoopReady, value))
				{
					return;
				}

				OnPropertyChanged(nameof(RadarLoadingText));

				// Reflectivity has finished rendering: speculatively build Velocity in the background so a
				// later switch to it is instant. Only on the false→true transition (once per load — the
				// flag is reset at each loop begin), and only for a real loop. Cheap if the user is already
				// on Velocity (frames are already building) — the JS side is idempotent.
				if (value && _frameCount > 0)
				{
					_ = _mapService.PrefetchRadarVelocityAsync();
					StartTiltPrefetch();
				}
			}
		}

		/// <summary>Scrubber maximum: the last frame index (0-based).</summary>
		public int MaxFrameIndex => _frameCount > 0 ? _frameCount - 1 : 0;

		/// <summary>The loop frame currently shown (0 = oldest, MaxFrameIndex = newest).</summary>
		public double CurrentFrameIndex
		{
			get => _currentFrameIndex;
			set
			{
				var clamped = (int)Math.Round(value < 0 ? 0 : (value > MaxFrameIndex ? MaxFrameIndex : value));
				if (!SetProperty(ref _currentFrameIndex, clamped))
				{
					return;
				}

				OnPropertyChanged(nameof(CurrentFrameTimeText));
				// Refresh the card readouts (frame N/M, time) NOW rather than waiting for the 1s tick —
				// otherwise at fast playback (≤500ms/frame) the "frame N/M" line only updates every
				// other frame and visibly lags the actual loop.
				RaiseRadarReadout();

				if (_isMapReady)
				{
					_ = _mapService.ShowRadarFrameAsync(clamped);
				}
			}
		}

		/// <summary>The displayed frame's local time (shown beside the transport). Empty until that frame's
		/// time is known — load progress is conveyed by the segmented scrubber now, not text.</summary>
		public string CurrentFrameTimeText
		{
			get
			{
				var t = (_currentFrameIndex >= 0 && _currentFrameIndex < _frameTimes.Length)
					? _frameTimes[_currentFrameIndex]
					: null;
				return t?.ToLocalTime().ToString("h:mm tt") ?? "";
			}
		}

		/// <summary>Segoe Fluent glyph for the play/pause button (pause when playing).</summary>
		public string PlayPauseGlyph => _isPlaying ? "" : "";

		// Whether the loop is "engaged" — playing OR paused mid-loop. Stop is offered in both states
		// (so you can stop from a pause), and disabled only once stopped/idle (or freshly loaded).
		private bool _loopEngaged;

		/// <summary>Whether the Stop button is enabled (the loop is playing or paused, not stopped).</summary>
		public bool CanStopLoop => _loopEngaged;

		private void SetLoopEngaged(bool value) =>
			SetProperty(ref _loopEngaged, value, nameof(CanStopLoop));

		/// <summary>Toggles loop playback (no-op until the loop is fully loaded). Pressing play OR
		/// pause engages the loop, so Stop becomes available (and stays available while paused).</summary>
		public void ToggleRadarPlay()
		{
			if (!_isLoopReady)
			{
				return;
			}

			IsPlaying = !_isPlaying;
			SetLoopEngaged(true);
		}

		/// <summary>Stops the loop: halts playback, disengages (disabling Stop), and returns to the
		/// newest frame. Enabled only while playing or paused.</summary>
		public void StopRadarLoop()
		{
			IsPlaying = false;
			SetLoopEngaged(false);
			CurrentFrameIndex = MaxFrameIndex; // snap back to the latest frame
		}

		/// <summary>Steps the shown frame by <paramref name="delta"/> (−1 = previous, +1 = next). Pauses
		/// playback so the step sticks (stepping is a manual seek), and keeps the loop engaged so Stop stays
		/// available. No-op until the loop is fully loaded. The CurrentFrameIndex setter clamps at the ends.</summary>
		public void StepFrame(int delta)
		{
			if (!_isLoopReady)
			{
				return;
			}

			if (_isPlaying)
			{
				IsPlaying = false; // pause in place so the manual step isn't immediately overwritten
			}

			SetLoopEngaged(true);
			CurrentFrameIndex = _currentFrameIndex + delta;
		}

		/// <summary>Opacity (0-1) of the radar layer. Driven by the ribbon's radar slider.</summary>
		public double RadarOpacity
		{
			get => _radarOpacity;
			set
			{
				if (SetProperty(ref _radarOpacity, value) && _isMapReady)
				{
					_ = _mapService.SetRadarOpacityAsync(value);
				}
			}
		}

		/// <summary>The radar products (moments) selectable in the Product combo — the single source the
		/// combo binds to, mirroring the JS registry in <c>radar-products.js</c>. <see cref="RadarProductOption.Id"/>
		/// must match the JS product id passed to <c>window.setRadarProduct</c>; <see cref="RadarProductOption.IsLazy"/>
		/// marks the expensive-to-build product (velocity) whose frames aren't display-ready until built.
		/// Adding a product = one entry here + the JS side (a build fn + ramp + registry entry).</summary>
		public IReadOnlyList<RadarProductOption> RadarProductOptions { get; } = new[]
		{
			new RadarProductOption("reflectivity", "Reflectivity", "Ref", false),
			new RadarProductOption("velocity", "Velocity", "Vel", true),
			new RadarProductOption("cc", "Correlation Coefficient", "CC", false),
			new RadarProductOption("kdp", "Specific Differential Phase", "KDP", false),
			new RadarProductOption("zdr", "Differential Reflectivity", "ZDR", false),
			new RadarProductOption("sw", "Spectrum Width", "SW", false),
		};

		/// <summary>
		/// Fans the WebView's full ramp table (radar-ramps.js, keyed by product id) onto the product
		/// options, so the Product combo can draw EVERY product's scale — not just the active one. Pushed
		/// once when the page loads; unknown ids are ignored and a product with no ramp simply draws none.
		/// </summary>
		public void SetAllRamps(IReadOnlyDictionary<string, RadarRampInfo>? ramps)
		{
			if (ramps is null) return;
			foreach (var option in RadarProductOptions)
			{
				option.Ramp = ramps.TryGetValue(option.Id, out var ramp) ? ramp : null;
			}
		}

		// Index into RadarProductOptions. Bound to the Radar console's Product combo (SelectedIndex).
		private int _radarProductIndex;

		/// <summary>Selected radar product index (0 = Reflectivity, 1 = Velocity, 2 = Correlation Coeff).</summary>
		public int RadarProductIndex
		{
			get => _radarProductIndex;
			set
			{
				if (!SetProperty(ref _radarProductIndex, value))
				{
					return;
				}

				// Re-derive the scrubber cells for the newly-active product. We deliberately do NOT blank
				// the velocity-ready set here: radar.js posts fresh build progress synchronously from
				// setProduct, so a moment later SetBuildProgress corrects it. With velocity now PREFETCHED
				// in the background (see PrefetchRadarVelocityAsync), it's usually already built when the
				// user switches, so the scrubber stays lit — instant, no blank flash. If it's still
				// building, the not-yet-built cells drop to "loading" and fill in, same as any load.
				RefreshSegmentReadiness();

				if (_isMapReady && value >= 0 && value < RadarProductOptions.Count)
				{
					_ = _mapService.SetRadarProductAsync(RadarProductOptions[value].Id);
				}
			}
		}

		// ===== Tilt (elevation) selection ===============================================================
		// Unlike a PRODUCT switch — which re-renders bytes already decoded in the WebView — a TILT switch
		// needs different bytes entirely: each cached .V06 holds exactly ONE tilt, which is why the JS
		// never learned about tilts (its Math.min(elevations) picks whatever tilt the file contains). So
		// changing tilt reloads the loop through the normal load path, just with a different tilt angle.
		//
		// The choices come from the VCP's designed elevation table, which rides in every cached tilt's
		// metadata — so the list populates from the newest frame with no extra fetch, and re-populates if
		// the radar changes VCP.

		// How many tilts the LIVE loop offers, counting up from the base.
		//
		// A radar scans bottom-up over a ~4.5-min volume, so a tilt's freshness floor is set by when the
		// antenna reaches it: the bottom ~4 are cut within the first ~2 min and the chunks feed can serve
		// them ~2-3 min old, but by 8°+ the tilt isn't scanned until ~4 min in and its best-case age has
		// converged on the archive's ~5-10 min — there's nothing left to win, so offering it would just
		// be shipping stale data behind a live-looking UI. 4 matches what RadarScope exposes.
		//
		// Past Event replay is NOT capped: every frame there is historical, so 19.5° from 2013 is exactly
		// as current as 0.5° from 2013 and there's no freshness to protect. See docs/radar-tilts.md.
		private const int LiveTiltCount = 4;

		// The full designed tilt list of the last-loaded volume's VCP, before the live cap. Retained so
		// the list can be rebuilt when the temporal mode flips (live <-> replay) without waiting for the
		// next frame to land.
		private IReadOnlyList<float> _vcpAngles = Array.Empty<float>();

		/// <summary>The tilts selectable for the current site + mode (base tilt first): the whole VCP in
		/// replay, the freshest <see cref="LiveTiltCount"/> in a live loop. Empty until the first frame
		/// loads, or when the VCP doesn't parse — the combo is then disabled rather than offering a
		/// guess.</summary>
		public ObservableCollection<RadarTiltOption> RadarTiltOptions { get; } = new();

		// The angle currently loaded; null = base tilt. This is the field the fetch path keys on, so it
		// must be updated BEFORE any load is started.
		private float? _selectedTiltAngle;

		private int _radarTiltIndex;

		/// <summary>Selected index into <see cref="RadarTiltOptions"/>. Bound to the Radar console's Tilt
		/// combo. Changing it reloads the loop at that elevation (see the region comment).</summary>
		public int RadarTiltIndex
		{
			get => _radarTiltIndex;
			set
			{
				if (value < 0 || value >= RadarTiltOptions.Count || !SetProperty(ref _radarTiltIndex, value))
				{
					return;
				}

				_selectedTiltAngle = RadarTiltOptions[value].Angle;
				OnPropertyChanged(nameof(SelectedTiltLabel));
				ReloadForTiltChange();
			}
		}

		/// <summary>The loaded tilt, for the Selected Site readout ("0.5°"). Empty with no loop.</summary>
		public string SelectedTiltLabel =>
			_radarTiltIndex >= 0 && _radarTiltIndex < RadarTiltOptions.Count
				? RadarTiltOptions[_radarTiltIndex].Label
				: string.Empty;

		/// <summary>Whether a tilt can be picked: a loop is up and its VCP offered more than one.</summary>
		public bool CanSelectTilt => RadarTiltOptions.Count > 1;

		// Rebuilds the tilt list from a freshly-loaded volume's VCP elevation table, preserving the
		// current selection BY ANGLE (a VCP change reorders/renumbers tilts, so an index would silently
		// jump to a different elevation). Falls back to the base tilt when the loaded angle is gone from
		// the new VCP — e.g. the radar dropped from precip to clear-air, which scans fewer tilts. No-op
		// when the list is unchanged, so this can be called per frame.
		//
		// Pass null to rebuild from the last-known VCP (used when the temporal mode flips, which changes
		// the cap but not the radar).
		private void UpdateTiltOptions(IReadOnlyList<float>? angles)
		{
			if (angles is { Count: > 0 })
			{
				_vcpAngles = angles;
			}
			angles = _vcpAngles;

			// Live loops only offer tilts the chunks feed can serve FRESH; replay offers the lot.
			if (!IsPastEventMode && angles.Count > LiveTiltCount)
			{
				angles = angles.Take(LiveTiltCount).ToList();
			}

			var next = new List<RadarTiltOption>();
			if (angles is { Count: > 0 })
			{
				// The lowest angle IS the base tilt, so it takes a NULL angle rather than its own value —
				// that null is what routes it to the cheap prefix fetch and the live frame. Labels are the
				// designed angles rounded for display (0.88° reads "0.9°"), but the ANGLE carried is the
				// unrounded table value, which is what the extractor matches against.
				next.Add(new RadarTiltOption($"{angles[0]:0.0}°", null));
				for (var i = 1; i < angles.Count; i++)
				{
					next.Add(new RadarTiltOption($"{angles[i]:0.0}°", angles[i]));
				}
			}
			else
			{
				// No VCP table (a legacy/raw volume): we're showing the lowest tilt but can't know what
				// else exists, so offer only that. CanSelectTilt is then false and the combo is disabled —
				// no guessing at tilts we can't fetch.
				next.Add(new RadarTiltOption("0.5°", null));
			}

			if (next.Count == RadarTiltOptions.Count
				&& next.Zip(RadarTiltOptions).All(p => p.First.Label == p.Second.Label))
			{
				return; // same VCP, same tilts
			}

			RadarTiltOptions.Clear();
			foreach (var option in next)
			{
				RadarTiltOptions.Add(option);
			}

			// Keep showing the same ELEVATION across a VCP change where possible.
			var keep = RadarTiltOptions
				.Select((o, i) => (o, i))
				.FirstOrDefault(p => Nullable.Equals(p.o.Angle, _selectedTiltAngle));
			_radarTiltIndex = keep.o is not null ? keep.i : 0;
			_selectedTiltAngle = RadarTiltOptions[_radarTiltIndex].Angle;

			OnPropertyChanged(nameof(RadarTiltIndex));
			OnPropertyChanged(nameof(SelectedTiltLabel));
			OnPropertyChanged(nameof(CanSelectTilt));
		}

		/// <summary>
		/// Whether the on-map radar site marker buttons are shown. Toggled by the ribbon's
		/// site-visibility button. Hiding the markers never affects an active radar loop.
		/// </summary>
		public bool RadarSitesVisible
		{
			get => _radarSitesVisible;
			set
			{
				if (!SetProperty(ref _radarSitesVisible, value))
				{
					return;
				}

				OnPropertyChanged(nameof(RadarSitesToggleLabel));

				if (_isMapReady)
				{
					_ = _mapService.SetRadarSitesVisibleAsync(value);
				}
			}
		}

		/// <summary>Label for the site-visibility toggle button, reflecting its action.</summary>
		public string RadarSitesToggleLabel => _radarSitesVisible ? "Hide Sites" : "Show Sites";

		/// <summary>Flips <see cref="RadarSitesVisible"/>; bound to the ribbon toggle button.</summary>
		public void ToggleRadarSitesVisible() => RadarSitesVisible = !_radarSitesVisible;

		/// <summary>
		/// Whether the research/test radar markers (e.g. KCRI) are shown — the "Show Research Radars"
		/// toggle. Off by default (an opt-in extra layer, mirroring RadarScope). Independent of the
		/// operational "Show Sites" toggle and of any active loop; a research site loads/renders
		/// through the same pipeline as an operational one.
		/// </summary>
		public bool ShowResearchRadars
		{
			get => _showResearchRadars;
			set
			{
				if (SetProperty(ref _showResearchRadars, value) && _isMapReady)
				{
					_ = _mapService.SetResearchRadarsVisibleAsync(value);
				}
			}
		}

		/// <summary>
		/// Whether the TDWR markers (the FAA Terminal Doppler Weather Radar `T***` network) are shown —
		/// the "Show TDWRs" toggle. Off by default (an opt-in extra layer, mirroring RadarScope).
		/// Independent of the operational "Show Sites" and "Show Research Radars" toggles and of any
		/// active loop; a TDWR loads/renders through the same pipeline as an operational site.
		/// </summary>
		public bool ShowTdwrs
		{
			get => _showTdwrs;
			set
			{
				if (SetProperty(ref _showTdwrs, value) && _isMapReady)
				{
					_ = _mapService.SetTdwrsVisibleAsync(value);
				}
			}
		}

		// App-lifetime 1s tick that advances the radar next-update progress bar.
		private async Task RunProgressTickAsync()
		{
			using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
			while (await timer.WaitForNextTickAsync())
			{
				OnPropertyChanged(nameof(RadarNextFrameProgress));
				OnPropertyChanged(nameof(RadarNextFrameText));
			}
		}

		/// <summary>Called by MapViewModel once the map page is ready: shows the radar site
		/// markers and starts the offline-status + progress loops.</summary>
		public async Task OnMapsReadyAsync()
		{
			_isMapReady = true;

			// Provide the radar sites as clickable on-map markers. `research`/`tdwr` flag the extra
			// networks so the page can gate them behind the "Show Research Radars" / "Show TDWRs" toggles.
			var sites = _radarSiteProvider.GetSites()
				.Select(s => new { id = s.Id, name = s.Name, lng = s.Longitude, lat = s.Latitude,
					research = s.Class == RadarSiteClass.Research, tdwr = s.Class == RadarSiteClass.Tdwr });
			await _mapService.ShowRadarSitesAsync(System.Text.Json.JsonSerializer.Serialize(sites));

			// Push the current extra-network visibility (off by default — the page defaults to hidden
			// too, but be explicit so the toggles and the page never disagree on startup).
			await _mapService.SetResearchRadarsVisibleAsync(_showResearchRadars);
			await _mapService.SetTdwrsVisibleAsync(_showTdwrs);

			// Flag sites with no recent data ("offline") and keep that refreshed.
			_ = RunSiteStatusLoopAsync();

			// Drive the next-update progress bar (radar live frame).
			_ = RunProgressTickAsync();
		}

		private async Task RunSiteStatusLoopAsync()
		{
			while (true)
			{
				await RefreshLiveSiteStatusAsync();
				await Task.Delay(TimeSpan.FromMinutes(10));
			}
		}

		// One live-availability pass: flag sites with no data in the live feed as "down". Skipped while
		// in Past Event mode — there the markers reflect the chosen date's availability instead (see
		// ApplyPastAvailabilityAsync), so we mustn't overwrite it with the live set.
		private async Task RefreshLiveSiteStatusAsync()
		{
			if (_isPastEventMode)
			{
				return;
			}
			try
			{
				var live = await _radarService.GetLiveSiteIdsAsync();
				if (!_isPastEventMode)
				{
					await ApplySiteAvailabilityAsync(live);
				}
			}
			catch
			{
				// Best effort: a failed status check just leaves the markers as they are.
			}
		}

		// Renders the given set of AVAILABLE site IDs as normal and the rest as "down" — on the on-map
		// markers and the list rows. Shared by the live loop and the past-event availability pass.
		private async Task ApplySiteAvailabilityAsync(IReadOnlyCollection<string> availableIds)
		{
			var available = new HashSet<string>(availableIds, StringComparer.OrdinalIgnoreCase);
			var offline = _radarSiteProvider.GetSites()
				.Select(s => s.Id)
				.Where(id => !available.Contains(id))
				.ToList();
			// These awaits resume on the UI thread, so updating the observable rows is safe.
			var offlineSet = new HashSet<string>(offline, StringComparer.OrdinalIgnoreCase);
			foreach (var row in RadarSiteRows)
			{
				row.IsOffline = offlineSet.Contains(row.Id);
			}
			if (_isMapReady)
			{
				await _mapService.SetRadarSitesStatusAsync(System.Text.Json.JsonSerializer.Serialize(offline));
			}
			Services.RadarDiagnostics.Log("vm", "site.status", ("offline", offline.Count),
				("ids", offline.Count is > 0 and <= 20 ? string.Join(",", offline) : null));
		}

		// Past Event Viewer: gray out sites that had no data on the window's UTC date(s), so you can see
		// availability before clicking. Guarded so a late response can't clobber a live view if the user
		// left past mode meanwhile. Best-effort (a failed listing just leaves sites shown as available).
		private async Task ApplyPastAvailabilityAsync(DateTimeOffset startUtc, DateTimeOffset endUtc)
		{
			try
			{
				var available = await _radarService.GetSiteIdsForDateAsync(startUtc, endUtc);
				if (_isPastEventMode)
				{
					await ApplySiteAvailabilityAsync(available);
				}
			}
			catch
			{
				// ignore
			}
		}
	}
}
