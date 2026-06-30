using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using OfflineMapsTest.Models;
using OfflineMapsTest.Services;

namespace OfflineMapsTest.ViewModels
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
	/// View model backing the map view. Owns the selectable basemap styles + current
	/// selection, the SPC outlook day/product selection and overlay opacity, the radar site
	/// selection + animation loop, and the collapsed state of the bottom control ribbon.
	/// Drives the map through <see cref="IMapService"/>.
	/// </summary>
	public sealed class MapViewModel : INotifyPropertyChanged
	{
		private readonly IMapService _mapService;
		private readonly IStyleProvider _styleProvider;
		private readonly IRegionProvider _regionProvider;
		private readonly ISpcOutlookService _spcOutlookService;
		private readonly ISpcWatchService _watchService;
		private readonly IRadarSiteProvider _radarSiteProvider;
		private readonly ILevel2RadarService _radarService;
		private readonly ILocationService _locationService;
		private readonly IDowEventProvider _dowEventProvider;

		// Readiness guard: the map page must have reported 'mapReady' before style /
		// outlook commands can succeed. The view calls OnMapsReadyAsync() once the map
		// loads; until then a style change is stored and the overlay is deferred.
		private bool _isMapReady;

		private MapStyle? _selectedStyle;

		// The region the main map is framed on (CONUS).
		private MapRegion? _mainRegion;

		// Selected SPC outlook day + product option. The option list cascades to
		// whatever's valid for the selected day (plus a leading "None" entry); selecting
		// an option shows that product on the map, or clears it for None.
		private int _selectedDay;
		private DayOption? _selectedDayOption;
		private IReadOnlyList<OutlookOption> _productOptions = new List<OutlookOption>();
		private OutlookOption? _selectedOption;
		// Master "show outlook layer" gate (Outlook tool-window toggle). Defaults OFF so the app
		// launches with no outlook drawn; flipping it on shows the armed Day/Product selection.
		private bool _isOutlookVisible;
		// SPC watch-box overlay toggle (Outlook tool window). Independent of the outlook; default
		// OFF so the app launches with no watch boxes drawn.
		private bool _showWatches;

		// Authoritative issued/valid/expire readout for the loaded outlook, parsed from
		// the product's cached GeoJSON. Empty when None is selected or no times are known.
		private string _outlookTimesText = string.Empty;

		// Fill opacity (0-1) for the outlook polygons; the outlines stay opaque so the
		// basemap reads through. Driven by the ribbon's opacity slider.
		private double _outlookOpacity = 0.05;

		// Suppresses outlook map updates while a day-change cascade re-selects the
		// option (the product combobox transiently nulls its selection mid-swap).
		private bool _suppressOutlookUpdate;

		// Selected radar site option ("None" clears the layer) + radar layer opacity.
		private RadarOption? _selectedRadarOption;
		private double _radarOpacity = 0.85;

		// Whether the on-map radar site marker buttons are shown. Independent of the radar
		// layer: hiding the markers leaves any active loop rendering.
		private bool _radarSitesVisible = true;

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
		private int _frameCount;
		private int _readyCount;
		private int _currentFrameIndex;
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
		private int _pastEventYearIndex = DateTime.Now.Year - PastEventStartYear;
		private int _pastEventMonthIndex = DateTime.Now.Month - 1;
		private int _pastEventDayIndex = DateTime.Now.Day - 1;
		private TimeSpan _pastEventTime = DateTimeOffset.Now.TimeOfDay;
		private int _pastEventDurationIndex = 1; // default 1 hour
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

		// Outlook refresh schedule (set by MainWindow each ~15-min cycle) for the Outlook tool
		// window's next-update progress bar.
		private DateTimeOffset? _outlookCycleStart;
		private DateTimeOffset? _nextOutlookRefreshAt;

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

		public MapViewModel(IMapService mapService, IStyleProvider styleProvider, IRegionProvider regionProvider, ISpcOutlookService spcOutlookService, ISpcWatchService watchService, IRadarSiteProvider radarSiteProvider, ILevel2RadarService radarService, ILocationService locationService, IDowEventProvider dowEventProvider)
		{
			_mapService = mapService;
			_styleProvider = styleProvider;
			_regionProvider = regionProvider;
			_spcOutlookService = spcOutlookService;
			_watchService = watchService;
			_radarSiteProvider = radarSiteProvider;
			_radarService = radarService;
			_locationService = locationService;
			_dowEventProvider = dowEventProvider;
			DowEvents = _dowEventProvider.GetEvents();

			AvailableStyles = _styleProvider.GetStyles();

			// Assign the backing field directly (not the setter) so the default
			// selection does NOT trigger a map command during construction. The page
			// loads this style via its URL, so there is nothing to re-apply. Default to
			// Data Viz Black.
			_selectedStyle = AvailableStyles.FirstOrDefault(s => s.Id == "dataVizBlack")
				?? AvailableStyles.FirstOrDefault();

			// The main map is framed on CONUS.
			var regions = _regionProvider.GetRegions();
			_mainRegion = regions.FirstOrDefault(r => r.Id == "conus") ?? regions.FirstOrDefault();

			// SPC outlook selectors. Day 1 Categorical is the armed default, but the visibility
			// toggle (IsOutlookVisible) defaults off, so nothing is drawn on launch — flipping
			// the toggle on shows this selection. Assign backing fields directly so construction
			// fires no map command (OnMapsReadyAsync applies the state once the map is ready).
			Days = BuildDayOptions(_spcOutlookService.AvailableDays);
			_selectedDayOption = Days.FirstOrDefault();
			_selectedDay = _selectedDayOption?.Day ?? 0;
			RebuildProductOptions();
			_selectedOption = DefaultOptionForDay();

			// Radar site selector: a leading "None" entry plus the curated sites. Defaults
			// to None so launch is unchanged; selecting a site loads its loop.
			var radarOptions = new List<RadarOption> { new("None", null) };
			radarOptions.AddRange(_radarSiteProvider.GetSites().Select(s => new RadarOption($"{s.Id} — {s.Name}", s)));
			RadarOptions = radarOptions;
			_selectedRadarOption = RadarOptions[0];

			// Observable rows for the dock's "Radar Sites" tool window (click a row to select).
			// Each wraps a site with view-facing state (offline → "down", not clickable); the
			// site→row lookup lets a map-marker pick highlight the matching row.
			var rows = _radarSiteProvider.GetSites().Select(s => new RadarSiteRow(s)).ToList();
			RadarSiteRows = rows;
			_rowBySite = rows.ToDictionary(r => r.Site);
		}

		/// <summary>Observable rows (site + offline state) for the dock's "Radar Sites" list.</summary>
		public IReadOnlyList<RadarSiteRow> RadarSiteRows { get; }

		private readonly IReadOnlyDictionary<RadarSite, RadarSiteRow> _rowBySite;

		public IReadOnlyList<MapStyle> AvailableStyles { get; }

		/// <summary>The region the main, full-window map is framed on.</summary>
		public MapRegion? MainRegion => _mainRegion;

		// ── Left tool-window dock (static mock of a VS-style dock). The whole pane collapses to
		//    a reveal button; the tool windows inside are fixed equal-height regions. ──
		private bool _isDockCollapsed; // start expanded so the dock is visible on launch

		/// <summary>Whether the left tool-window dock is collapsed (hidden behind its reveal button).</summary>
		public bool IsDockCollapsed
		{
			get => _isDockCollapsed;
			set
			{
				if (_isDockCollapsed == value)
				{
					return;
				}
				_isDockCollapsed = value;
				OnPropertyChanged();
				OnPropertyChanged(nameof(IsDockExpanded));
			}
		}

		/// <summary>Convenience inverse of <see cref="IsDockCollapsed"/>.</summary>
		public bool IsDockExpanded => !_isDockCollapsed;

		/// <summary>Toggles the dock open/closed; bound to the collapse + reveal buttons.</summary>
		public void ToggleDock() => IsDockCollapsed = !_isDockCollapsed;

		/// <summary>Outlook days that have products (1-8), each labeled with its date;
		/// static for the app lifetime.</summary>
		public IReadOnlyList<DayOption> Days { get; }

		/// <summary>
		/// The selected outlook day (carrying its date label). Changing it cascades the
		/// product list and auto-selects a product for the new day so an overlay stays
		/// visible.
		/// </summary>
		public DayOption? SelectedDayOption
		{
			get => _selectedDayOption;
			set
			{
				if (_selectedDayOption == value || value is null)
				{
					return;
				}

				_selectedDayOption = value;
				_selectedDay = value.Day;
				OnPropertyChanged();

				// Rebuild the option list, then re-select an option for the new day.
				// Suppress overlay updates during the swap (the product combobox briefly
				// nulls its selection as its items change) and push one update at the end.
				_suppressOutlookUpdate = true;
				RebuildProductOptions();
				SelectedOption = DefaultOptionForDay();
				_suppressOutlookUpdate = false;

				ApplyCurrentOutlook();
			}
		}

		/// <summary>
		/// Options for the product selector: a leading "None" entry (clears the overlay)
		/// followed by the products valid for <see cref="SelectedDay"/>.
		/// </summary>
		public IReadOnlyList<OutlookOption> ProductOptions => _productOptions;

		/// <summary>
		/// The selected option. Setting it shows that product on the map, or clears the
		/// overlay for the "None" option, once the map is ready.
		/// </summary>
		public OutlookOption? SelectedOption
		{
			get => _selectedOption;
			set
			{
				if (_selectedOption == value)
				{
					return;
				}

				_selectedOption = value;
				OnPropertyChanged();
				OnPropertyChanged(nameof(SelectedProduct));

				if (!_suppressOutlookUpdate)
				{
					ApplyCurrentOutlook();
				}
			}
		}

		/// <summary>The product behind the current selection (null for "None").</summary>
		public SpcOutlookProduct? SelectedProduct => _selectedOption?.Product;

		/// <summary>
		/// Master on/off gate for the outlook overlay (bound to the Outlook tool-window toggle).
		/// Independent of the Day/Product selection: when off, no outlook is drawn even if a
		/// product is selected; when on, the selected product (if any) is shown. Defaults off so
		/// the app launches with no outlook on the map.
		/// </summary>
		public bool IsOutlookVisible
		{
			get => _isOutlookVisible;
			set
			{
				if (_isOutlookVisible == value)
				{
					return;
				}

				_isOutlookVisible = value;
				OnPropertyChanged();
				ApplyCurrentOutlook();
			}
		}

		/// <summary>Show the SPC watch boxes — Tornado / Severe Thunderstorm Watches — on the map
		/// (Outlook tool-window toggle, default off).</summary>
		public bool ShowWatches
		{
			get => _showWatches;
			set
			{
				if (_showWatches == value)
				{
					return;
				}

				_showWatches = value;
				OnPropertyChanged();
				if (_isMapReady)
				{
					_ = _mapService.SetWatchesVisibleAsync(value);
				}
			}
		}

		/// <summary>
		/// Re-pushes the watch source URL to the page so it re-fetches the freshly-cached boxes.
		/// Called by the view after a background watch refresh; the page only re-fetches when the
		/// watch layer is shown.
		/// </summary>
		public void OnWatchesRefreshed()
		{
			if (_isMapReady)
			{
				_ = _mapService.SetWatchSourceAsync(_watchService.WatchesUrl);
			}
		}

		/// <summary>
		/// Authoritative "Issued … · Valid … → …" line for the loaded outlook, parsed from
		/// the product's cached GeoJSON (local time). Empty when None is selected or the
		/// cache has no times yet; bound to a readout that hides while empty.
		/// </summary>
		public string OutlookTimesText
		{
			get => _outlookTimesText;
			private set
			{
				if (_outlookTimesText == value)
				{
					return;
				}

				_outlookTimesText = value;
				OnPropertyChanged();
				OnPropertyChanged(nameof(HasOutlookTimes));
			}
		}

		/// <summary>Whether an issued/valid readout is available to show.</summary>
		public bool HasOutlookTimes => _outlookTimesText.Length > 0;

		// ── SPC outlook info card (shown while an outlook, not "None", is selected). Title +
		//    issued/effective times come from the cached GeoJSON; the forecast-discussion text is
		//    fetched lazily from SPC's HTML page (GetNarrativeAsync). All recomputed in
		//    UpdateOutlookCard when the selection changes or the outlook cache refreshes. ──
		private string _outlookCardTitle = string.Empty;
		private string _outlookIssuedText = string.Empty;
		private string _outlookValidText = string.Empty;
		private string _outlookNarrative = string.Empty;

		/// <summary>Whether an outlook is actually shown (a product selected AND the layer toggled
		/// on) — drives the Outlook Details window's visibility.</summary>
		public bool HasOutlookCard => _isOutlookVisible && _selectedOption?.Product is not null;

		/// <summary>Card header for the selected outlook, e.g. "Day 1 · Tornado".</summary>
		public string OutlookCardTitle => _outlookCardTitle;

		/// <summary>When the outlook was issued (local), or "—".</summary>
		public string OutlookIssuedText => _outlookIssuedText;

		/// <summary>The outlook's valid window (local), or "—".</summary>
		public string OutlookValidText => _outlookValidText;

		/// <summary>SPC forecast-discussion text for the selected outlook (or a status line).</summary>
		public string OutlookNarrativeText
		{
			get => _outlookNarrative;
			private set
			{
				if (_outlookNarrative == value)
				{
					return;
				}
				_outlookNarrative = value;
				OnPropertyChanged();
			}
		}

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
				if (_selectedRadarOption == value)
				{
					return;
				}

				_selectedRadarOption = value;
				OnPropertyChanged();
				// Mirror the selection to the dock list (so a map-marker pick highlights its row).
				// Guarded so the list's own setter doesn't bounce back into a re-select.
				_syncingSelection = true;
				SelectedSiteRow = value?.Site is { } site && _rowBySite.TryGetValue(site, out var row) ? row : null;
				_syncingSelection = false;
				_dowShowing = false; // a NEXRAD selection takes over the radar layer from any DOW frame
				OnPropertyChanged(nameof(HasRadarLoop));
				OnPropertyChanged(nameof(HasRadarDisplay));
				OnPropertyChanged(nameof(HasColorScale));
				RaiseRadarCard();
				// In Past Event mode a site pick just sets the target (the Load button drives the
				// replay) and clears any current loop; in Live mode it starts the live loop as usual.
				if (_isPastEventMode)
				{
					_ = SelectPastSiteAsync(value?.Site);
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
		public bool HasRadarDisplay => HasRadarLoop || _dowShowing;

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

		// ── DOW Event Viewer (curated mobile-radar frames; see tools/dow_import.py) ──
		// A separate, offline-curated path from the NEXRAD loop: a bundled .dow.json frame is decoded
		// and rendered through the SAME RadarLayer pipeline (reflectivity / velocity / CC + Inspect +
		// legend), but it's a single mobile-radar sweep at the truck's position — no loop/live/site
		// machinery. The frames are listed by the provider; loading one takes over the radar layer.
		private int _dowEventIndex;
		private string _dowStatus = string.Empty;
		private int _dowProductIndex; // 0 = reflectivity, 1 = velocity
		private bool _dowShowing;     // a DOW frame is currently displayed (separate from a NEXRAD loop)

		/// <summary>The curated DOW frames available to view (empty until events are converted + bundled).</summary>
		public IReadOnlyList<DowEvent> DowEvents { get; private set; } = System.Array.Empty<DowEvent>();

		/// <summary>True when at least one DOW event is bundled (drives the tool window's enabled state).</summary>
		public bool HasDowEvents => DowEvents.Count > 0;

		/// <summary>Selected DOW event (index into <see cref="DowEvents"/>).</summary>
		public int DowEventIndex
		{
			get => _dowEventIndex;
			set
			{
				var c = DowEvents.Count == 0 ? 0 : Math.Clamp(value, 0, DowEvents.Count - 1);
				if (_dowEventIndex != c) { _dowEventIndex = c; OnPropertyChanged(); }
			}
		}

		/// <summary>Rendered DOW moment: 0 = reflectivity, 1 = velocity. Applies live to a shown frame.</summary>
		public int DowProductIndex
		{
			get => _dowProductIndex;
			set
			{
				var c = Math.Clamp(value, 0, 1);
				if (_dowProductIndex == c) return;
				_dowProductIndex = c;
				OnPropertyChanged();
				_ = _mapService.SetRadarProductAsync(c == 1 ? "velocity" : "reflectivity");
			}
		}

		/// <summary>Product choices for the DOW Event Viewer (matches <see cref="DowProductIndex"/>).</summary>
		public IReadOnlyList<string> DowProductOptions { get; } = new[] { "Reflectivity", "Velocity" };

		/// <summary>Transient status line for the DOW Event Viewer.</summary>
		public string DowStatus
		{
			get => _dowStatus;
			private set { if (_dowStatus != value) { _dowStatus = value; OnPropertyChanged(); } }
		}

		/// <summary>Loads + shows the selected DOW frame (decoded in the WebView via the radar pipeline).</summary>
		public async Task LoadDowEventAsync()
		{
			if (DowEvents.Count == 0)
			{
				DowStatus = "No DOW events bundled yet — convert one with tools/dow_import.py and add it to Assets/DowEvents.";
				return;
			}

			var ev = DowEvents[Math.Clamp(_dowEventIndex, 0, DowEvents.Count - 1)];
			DowStatus = $"Loading {ev.Label}…";
			try
			{
				await _mapService.ShowDowFrameAsync(ev.Url);
				await _mapService.SetRadarProductAsync(_dowProductIndex == 1 ? "velocity" : "reflectivity");
				DowStatus = $"Showing {ev.Label}";
				_dowShowing = true;
				OnPropertyChanged(nameof(HasRadarDisplay)); // enable Inspect + Color Scale for the DOW frame
				OnPropertyChanged(nameof(HasColorScale));
			}
			catch (Exception ex)
			{
				DowStatus = $"Load failed: {ex.Message}";
			}
		}

		/// <summary>Clears the shown DOW frame.</summary>
		public async Task ClearDowEventAsync()
		{
			await _mapService.ClearDowFrameAsync();
			_dowShowing = false;
			OnPropertyChanged(nameof(HasRadarDisplay));
			OnPropertyChanged(nameof(HasColorScale));
			DowStatus = string.Empty;
		}

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
			set { var c = Math.Clamp(value, 0, PastEventYearOptions.Count - 1); if (_pastEventYearIndex != c) { _pastEventYearIndex = c; OnPropertyChanged(); } }
		}

		/// <summary>Selected month index (0-11).</summary>
		public int PastEventMonthIndex
		{
			get => _pastEventMonthIndex;
			set { var c = Math.Clamp(value, 0, 11); if (_pastEventMonthIndex != c) { _pastEventMonthIndex = c; OnPropertyChanged(); } }
		}

		/// <summary>Selected day index (0-30).</summary>
		public int PastEventDayIndex
		{
			get => _pastEventDayIndex;
			set { var c = Math.Clamp(value, 0, 30); if (_pastEventDayIndex != c) { _pastEventDayIndex = c; OnPropertyChanged(); } }
		}

		/// <summary>
		/// When true, the app is in historical-replay mode: live controls gray out, a site pick just
		/// targets the Load action, and toggling off clears the radar to idle.
		/// </summary>
		public bool IsPastEventMode
		{
			get => _isPastEventMode;
			set
			{
				if (_isPastEventMode == value)
				{
					return;
				}

				_isPastEventMode = value;
				OnPropertyChanged();
				OnPropertyChanged(nameof(IsLiveControlsEnabled));
				// Both directions clear to a clean slate (entering: drop the live loop; leaving:
				// drop the replay loop and go idle). Setting "None" routes through the mode-aware
				// SelectedRadarOption setter, which clears the loop without starting anything.
				SelectedRadarOption = RadarOptions[0];
				PastEventStatus = value ? "Pick a site, set a start time, then Load." : string.Empty;
			}
		}

		/// <summary>Inverse of <see cref="IsPastEventMode"/>; bound to IsEnabled on the live-only controls.</summary>
		public bool IsLiveControlsEnabled => !_isPastEventMode;

		/// <summary>Start time-of-day of the replay window (bound to a TimePicker, local time).</summary>
		public TimeSpan PastEventTime
		{
			get => _pastEventTime;
			set { if (_pastEventTime != value) { _pastEventTime = value; OnPropertyChanged(); } }
		}

		/// <summary>Selected window-duration index (into <see cref="PastEventDurationOptions"/>).</summary>
		public int PastEventDurationIndex
		{
			get => _pastEventDurationIndex;
			set
			{
				var clamped = Math.Clamp(value, 0, PastEventMinutesByIndex.Length - 1);
				if (_pastEventDurationIndex == clamped) return;
				_pastEventDurationIndex = clamped;
				OnPropertyChanged();
			}
		}

		/// <summary>Status line for the Past Event Viewer (loading / loaded N frames / errors).</summary>
		public string PastEventStatus
		{
			get => _pastEventStatus;
			private set { if (_pastEventStatus != value) { _pastEventStatus = value; OnPropertyChanged(); } }
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
				if (_loopLengthIndex == clamped) return;
				_loopLengthIndex = clamped;
				OnPropertyChanged();
				if (_selectedRadarOption?.Site is { } site) _ = StartRadarLoopAsync(site);
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
				if (_refreshIntervalIndex == clamped) return;
				_refreshIntervalIndex = clamped;
				OnPropertyChanged();
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
				if (_playbackSpeedIndex == clamped) return;
				_playbackSpeedIndex = clamped;
				OnPropertyChanged();
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
				if (ReferenceEquals(_selectedSiteRow, value)) return;
				_selectedSiteRow = value;
				OnPropertyChanged();
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
				if (_isLoopReady == value)
				{
					return;
				}

				_isLoopReady = value;
				OnPropertyChanged();
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
				if (_currentFrameIndex == clamped)
				{
					return;
				}

				_currentFrameIndex = clamped;
				OnPropertyChanged();
				OnPropertyChanged(nameof(CurrentFrameTimeText));
				// Refresh the card readouts (frame N/M, time) NOW rather than waiting for the 1s tick —
				// otherwise at fast playback (≤500ms/frame) the "frame N/M" line only updates every
				// other frame and visibly lags the actual loop.
				RaiseRadarCard();

				if (_isMapReady)
				{
					_ = _mapService.ShowRadarFrameAsync(clamped);
				}
			}
		}

		/// <summary>Label for the current frame: the volume's local time, or load progress.</summary>
		public string CurrentFrameTimeText
		{
			get
			{
				if (!_isLoopReady)
				{
					return _frameCount > 0 ? $"Loading {_readyCount}/{_frameCount}…" : "";
				}

				var t = (_currentFrameIndex >= 0 && _currentFrameIndex < _frameTimes.Length)
					? _frameTimes[_currentFrameIndex]
					: null;
				return t?.ToLocalTime().ToString("h:mm tt") ?? "";
			}
		}

		// ── Polished radar card (the user-facing presentation). The monospace RadarDebugText
		//    above feeds the collapsible "Diagnostics" expander; these are the headline values.
		//    All recomputed together via RaiseRadarCard (frame changes + the 1s tick). ──

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
				if (t is { } x) return x.ToLocalTime().ToString("h:mm tt");
				return _frameCount > 0 ? $"Loading {_readyCount}/{_frameCount}…" : "Loading…";
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

		/// <summary>Scan mode (VCP/precip/SAILS) from the latest live poll.</summary>
		public string RadarModeText
		{
			get
			{
				if (_selectedRadarOption?.Site is null) return string.Empty;
				return _liveModeText ?? (_isLoopReady ? "—" : "loading…");
			}
		}

		/// <summary>How old the freshest available frame is, e.g. "2 min ago" / "66 min ago".</summary>
		public string RadarAgeText
		{
			get
			{
				if (NewestFrameTime() is not { } t) return "—";
				var mins = (DateTimeOffset.Now - t).TotalMinutes;
				return mins < 1 ? "just now" : $"{mins:0} min ago";
			}
		}

		/// <summary>Loop coverage, e.g. "12:36 – 1:12 PM · 10 frames".</summary>
		public string RadarLoopSpanText
		{
			get
			{
				if (_selectedRadarOption?.Site is null || _archiveCount == 0) return string.Empty;
				var oldest = _frameTimes.Length > 0 ? _frameTimes[0] : null;
				var newest = (_archiveCount - 1 < _frameTimes.Length) ? _frameTimes[_archiveCount - 1] : null;
				static string L(DateTimeOffset? x) => x?.ToLocalTime().ToString("h:mm tt") ?? "—";
				return $"{L(oldest)} – {L(newest)} · {_frameCount} frames";
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

		// ── Next-update progress indicators (radar live frame + SPC outlook). A 1s UI tick
		//    (RunProgressTickAsync) re-raises these so the bars fill smoothly. ──

		// Elapsed fraction (0..100) of the current wait — 0 right after an update, ~100 just before
		// the next. Returns 0 when there's nothing scheduled (e.g. no loop / between cycles).
		private static double ProgressOf(DateTimeOffset? start, DateTimeOffset? next)
		{
			if (start is not { } s || next is not { } n) return 0;
			var total = (n - s).TotalSeconds;
			if (total <= 0) return 0;
			return Math.Clamp((DateTimeOffset.Now - s).TotalSeconds / total * 100.0, 0, 100);
		}

		private static string CountdownOf(DateTimeOffset? next)
		{
			if (next is not { } n) return "";
			var rem = (n - DateTimeOffset.Now).TotalSeconds;
			if (rem <= 0) return "updating…";
			return rem >= 90 ? $"next ~{rem / 60:0} min" : $"next ~{rem:0}s";
		}

		/// <summary>Progress (0-100) toward the next live-frame poll, for the Selected Site bar.</summary>
		public double RadarNextFrameProgress => ProgressOf(_livePollCycleStart, _nextLivePollAt);

		/// <summary>Countdown label to the next live-frame poll (e.g. "next ~12s").</summary>
		public string RadarNextFrameText => _selectedRadarOption?.Site is null ? "" : CountdownOf(_nextLivePollAt);

		/// <summary>Progress (0-100) toward the next SPC outlook refresh, for the Outlook bar.</summary>
		public double OutlookNextUpdateProgress => ProgressOf(_outlookCycleStart, _nextOutlookRefreshAt);

		/// <summary>Countdown label to the next SPC outlook refresh (e.g. "next ~9 min").</summary>
		public string OutlookNextUpdateText => CountdownOf(_nextOutlookRefreshAt);

		/// <summary>Called by MainWindow after each outlook refresh with the next refresh schedule.</summary>
		public void SetOutlookRefreshSchedule(DateTimeOffset cycleStart, DateTimeOffset next)
		{
			_outlookCycleStart = cycleStart;
			_nextOutlookRefreshAt = next;
			OnPropertyChanged(nameof(OutlookNextUpdateProgress));
			OnPropertyChanged(nameof(OutlookNextUpdateText));
		}

		// App-lifetime 1s tick that advances both next-update progress bars (independent of the
		// loop tick, since the outlook bar must update even when no radar loop is active).
		private async Task RunProgressTickAsync()
		{
			using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
			while (await timer.WaitForNextTickAsync())
			{
				OnPropertyChanged(nameof(RadarNextFrameProgress));
				OnPropertyChanged(nameof(RadarNextFrameText));
				OnPropertyChanged(nameof(OutlookNextUpdateProgress));
				OnPropertyChanged(nameof(OutlookNextUpdateText));
			}
		}

		// Raises the polished card properties + the diagnostics text together (frame changes,
		// the 1s tick, selection changes). One call so notifications can't drift out of sync.
		private void RaiseRadarCard()
		{
			OnPropertyChanged(nameof(RadarCardTitle));
			OnPropertyChanged(nameof(RadarCardCoords));
			OnPropertyChanged(nameof(RadarCardTime));
			OnPropertyChanged(nameof(RadarFrameDetail));
			OnPropertyChanged(nameof(RadarModeText));
			OnPropertyChanged(nameof(RadarAgeText));
			OnPropertyChanged(nameof(RadarLoopSpanText));
			OnPropertyChanged(nameof(RadarStatus));
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

		/// <summary>Segoe Fluent glyph for the play/pause button (pause when playing).</summary>
		public string PlayPauseGlyph => _isPlaying ? "" : "";

		// Whether the loop is "engaged" — playing OR paused mid-loop. Stop is offered in both states
		// (so you can stop from a pause), and disabled only once stopped/idle (or freshly loaded).
		private bool _loopEngaged;

		/// <summary>Whether the Stop button is enabled (the loop is playing or paused, not stopped).</summary>
		public bool CanStopLoop => _loopEngaged;

		private void SetLoopEngaged(bool value)
		{
			if (_loopEngaged == value) return;
			_loopEngaged = value;
			OnPropertyChanged(nameof(CanStopLoop));
		}

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

		/// <summary>Opacity (0-1) of the radar layer. Driven by the ribbon's radar slider.</summary>
		public double RadarOpacity
		{
			get => _radarOpacity;
			set
			{
				if (_radarOpacity == value)
				{
					return;
				}

				_radarOpacity = value;
				OnPropertyChanged();

				if (_isMapReady)
				{
					_ = _mapService.SetRadarOpacityAsync(value);
				}
			}
		}

		// 0 = reflectivity, 1 = velocity, 2 = correlation coefficient. Bound to the Radar Loop
		// tool window's Product combo.
		private int _radarProductIndex;

		/// <summary>Selected radar product index (0 = Reflectivity, 1 = Velocity, 2 = Correlation Coeff).</summary>
		public int RadarProductIndex
		{
			get => _radarProductIndex;
			set
			{
				if (_radarProductIndex == value)
				{
					return;
				}

				_radarProductIndex = value;
				OnPropertyChanged();

				if (_isMapReady)
				{
					_ = _mapService.SetRadarProductAsync(value switch { 1 => "velocity", 2 => "cc", _ => "reflectivity" });
				}
			}
		}

		// ── Color-scale legend. Fed by the WebView pushing the active product's ramp (from
		//    radar-ramps.js, the single source of truth) — never hard-coded here. Updates whenever the
		//    product changes; the Color Scale tool window renders the bar from these exact stops. ──
		private RadarRampInfo? _currentRamp;

		/// <summary>The active product's color ramp (or null until the first push). Drives the legend.</summary>
		public RadarRampInfo? CurrentRamp => _currentRamp;

		/// <summary>Whether to show the color-scale legend (a product ramp is known + a loop is active).</summary>
		public bool HasColorScale => _currentRamp is not null && HasRadarDisplay;

		/// <summary>Legend heading, e.g. "Reflectivity (dBZ)".</summary>
		public string RampTitle => _currentRamp is { } r ? $"{r.Label} ({r.Unit})" : string.Empty;

		/// <summary>Legend tick labels at the low / mid / high ends of the scale.</summary>
		public string RampMinText => _currentRamp is { } r ? FormatRampValue(r.Min) : string.Empty;
		public string RampMidText => _currentRamp is { } r ? FormatRampValue((r.Min + r.Max) / 2) : string.Empty;
		public string RampMaxText => _currentRamp is { } r ? FormatRampValue(r.Max) : string.Empty;

		private static string FormatRampValue(double v) =>
			v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

		/// <summary>Called from the view when the WebView pushes the active product's ramp.</summary>
		public void SetColorScale(RadarRampInfo? ramp)
		{
			_currentRamp = ramp;
			OnPropertyChanged(nameof(CurrentRamp));
			OnPropertyChanged(nameof(HasColorScale));
			OnPropertyChanged(nameof(RampTitle));
			OnPropertyChanged(nameof(RampMinText));
			OnPropertyChanged(nameof(RampMidText));
			OnPropertyChanged(nameof(RampMaxText));
			OnPropertyChanged(nameof(IsInspectMarkerVisible));
		}

		// ── Inspector ("read the value under the cursor", RadarScope-style). The toggle drives the
		//    WebView's inspect mode; the WebView pushes the value under the pointer back (SetInspectValue),
		//    which positions a live marker on the Color Scale bar. The value tooltip itself is drawn in
		//    the WebView next to the cursor (instant, no host round-trip per mouse move). ──
		private bool _isInspecting;
		private bool _hasInspectValue;
		private double _inspectFraction;
		private string _inspectValueText = string.Empty;

		/// <summary>Whether inspect mode is engaged (Radar Loop tool window toggle).</summary>
		public bool IsInspecting
		{
			get => _isInspecting;
			set
			{
				if (_isInspecting == value)
				{
					return;
				}

				_isInspecting = value;
				OnPropertyChanged();
				if (!value)
				{
					SetInspectValue(null); // clear the marker when leaving inspect
				}
				OnPropertyChanged(nameof(IsInspectMarkerVisible));

				if (_isMapReady)
				{
					_ = _mapService.SetRadarInspectAsync(value);
				}
			}
		}

		/// <summary>Whether the live inspect marker should be shown on the color-scale bar.</summary>
		public bool IsInspectMarkerVisible => _isInspecting && _hasInspectValue && HasColorScale;

		/// <summary>Position (0-1) of the inspected value along the active ramp.</summary>
		public double InspectFraction => _inspectFraction;

		/// <summary>The inspected value formatted with the product unit (e.g. "47.5 dBZ").</summary>
		public string InspectValueText => _inspectValueText;

		/// <summary>Called from the view when the WebView pushes the value under the cursor (null = none).</summary>
		public void SetInspectValue(double? value)
		{
			if (value is double v && _currentRamp is { } r)
			{
				double span = r.Max - r.Min;
				if (span <= 0)
				{
					span = 1;
				}

				_inspectFraction = Math.Clamp((v - r.Min) / span, 0, 1);
				_inspectValueText = v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) +
					(string.IsNullOrEmpty(r.Unit) ? string.Empty : " " + r.Unit);
				_hasInspectValue = true;
			}
			else
			{
				_hasInspectValue = false;
			}

			OnPropertyChanged(nameof(InspectFraction));
			OnPropertyChanged(nameof(InspectValueText));
			OnPropertyChanged(nameof(IsInspectMarkerVisible));
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
				if (_radarSitesVisible == value)
				{
					return;
				}

				_radarSitesVisible = value;
				OnPropertyChanged();
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
		/// Fill opacity (0-1) of the outlook polygons. The outlines stay opaque, so
		/// lowering this lets the basemap and its borders read through the fill.
		/// </summary>
		public double OutlookOpacity
		{
			get => _outlookOpacity;
			set
			{
				if (_outlookOpacity == value)
				{
					return;
				}

				_outlookOpacity = value;
				OnPropertyChanged();

				if (_isMapReady)
				{
					_ = _mapService.SetOutlookOpacityAsync(value);
				}
			}
		}

		// Rebuilds the per-day option list ("None" + the day's products).
		private void RebuildProductOptions()
		{
			var options = new List<OutlookOption> { new("None", null) };
			options.AddRange(_spcOutlookService
				.GetProductsForDay(_selectedDay)
				.Select(p => new OutlookOption(p.TypeLabel, p)));
			_productOptions = options;
			OnPropertyChanged(nameof(ProductOptions));
		}

		// The option to show for the current day: its Categorical if present (days 1-3),
		// else the day's first real product (days 4-8 lead with Probabilistic).
		private OutlookOption DefaultOptionForDay() =>
			_productOptions.FirstOrDefault(o => o.Product?.Type == SpcOutlookType.Categorical)
			?? _productOptions.FirstOrDefault(o => o.Product is not null)
			?? _productOptions[0];

		// Pushes the current selection to the map (show product, or clear for None),
		// once the map is ready.
		private void ApplyCurrentOutlook()
		{
			if (!_isMapReady)
			{
				return;
			}

			var product = _selectedOption?.Product;
			if (product is not null && _isOutlookVisible)
			{
				_ = _mapService.ShowOutlookAsync(product);
			}
			else
			{
				_ = _mapService.ClearOutlookAsync();
			}

			UpdateOutlookTimes();
		}

		// Labels each outlook day with the calendar date it covers (Day N = today + N-1,
		// local), e.g. "Day 1 · Sat Jun 14". A close-enough mapping for orientation; the
		// authoritative issued/valid times come from the loaded GeoJSON (UpdateOutlookTimes).
		private static IReadOnlyList<DayOption> BuildDayOptions(IReadOnlyList<int> days)
		{
			var today = DateTime.Now.Date;
			return days
				.Select(d => new DayOption(d, $"Day {d} · {today.AddDays(d - 1):ddd MMM d}"))
				.ToList();
		}

		// Reads the issued/valid/expire times for the current product from its cached GeoJSON
		// (local time) and updates both the ribbon readout and the info card. The ribbon line is
		// cleared when None is selected or no times are available; the card follows the selection.
		private void UpdateOutlookTimes()
		{
			// When the layer is toggled off, treat the selection as "none" so the times readout,
			// the Outlook Details window, and HasOutlookCard all reflect what's actually on the map.
			var product = _isOutlookVisible ? _selectedOption?.Product : null;
			var times = product is null ? null : _spcOutlookService.GetTimesForProduct(product);

			if (times is null)
			{
				OutlookTimesText = string.Empty;
			}
			else
			{
				var parts = new List<string>(2);
				if (times.Issued is { } issued)
				{
					parts.Add($"Issued {issued.ToLocalTime():ddd h:mm tt}");
				}
				if (times.Valid is { } valid && times.Expire is { } expire)
				{
					parts.Add($"Valid {valid.ToLocalTime():ddd h:mm tt} → {expire.ToLocalTime():ddd h:mm tt}");
				}
				OutlookTimesText = string.Join("  ·  ", parts);
			}

			UpdateOutlookCard(product, times);
		}

		// Updates the SPC outlook info card: title + issued/effective from the cached times, and
		// kicks off the (lazy, disk-cached) forecast-discussion fetch. Cleared for "None".
		private void UpdateOutlookCard(SpcOutlookProduct? product, SpcOutlookTimes? times)
		{
			if (product is null)
			{
				_outlookCardTitle = string.Empty;
				_outlookIssuedText = string.Empty;
				_outlookValidText = string.Empty;
				_narrativeFor = null;
				OutlookNarrativeText = string.Empty;
			}
			else
			{
				_outlookCardTitle = $"Day {product.Day} · {product.TypeLabel}";
				_outlookIssuedText = times?.Issued is { } iss
					? iss.ToLocalTime().ToString("ddd MMM d · h:mm tt")
					: "—";
				_outlookValidText = times?.Valid is { } v && times?.Expire is { } e
					? $"{v.ToLocalTime():ddd h:mm tt} → {e.ToLocalTime():ddd h:mm tt}"
					: "—";
				_ = RefreshOutlookNarrativeAsync(product);
			}

			OnPropertyChanged(nameof(HasOutlookCard));
			OnPropertyChanged(nameof(OutlookCardTitle));
			OnPropertyChanged(nameof(OutlookIssuedText));
			OnPropertyChanged(nameof(OutlookValidText));
		}

		// The product the current narrative belongs to — so a same-product refresh updates the
		// text silently, while a product switch shows the "Loading…" placeholder.
		private SpcOutlookProduct? _narrativeFor;

		// Fetches the SPC forecast discussion (network → disk cache) and shows it, unless the user
		// changed selection while it loaded.
		private async Task RefreshOutlookNarrativeAsync(SpcOutlookProduct product)
		{
			if (!ReferenceEquals(_narrativeFor, product))
			{
				OutlookNarrativeText = "Loading forecast discussion…";
			}

			string? text = null;
			try
			{
				text = await _spcOutlookService.GetNarrativeAsync(product);
			}
			catch
			{
				// Best effort; fall through to the not-available message.
			}

			if (!ReferenceEquals(_selectedOption?.Product, product))
			{
				return; // selection changed mid-fetch
			}
			_narrativeFor = product;
			OutlookNarrativeText = text ?? "Forecast discussion isn't available for this product yet.";
		}

		/// <summary>
		/// Called after the launch outlook refresh finishes: re-applies the current
		/// selection so a first-run (empty cache) overlay appears and the issued/valid
		/// readout picks up the freshly-written times.
		/// </summary>
		public void OnOutlooksRefreshed() => ApplyCurrentOutlook();

		public MapStyle? SelectedStyle
		{
			get => _selectedStyle;
			set
			{
				if (ReferenceEquals(_selectedStyle, value))
				{
					return;
				}

				_selectedStyle = value;
				OnPropertyChanged();

				// Only push a style change once the map can receive it. Pre-ready
				// selections are stored and applied later by OnMapsReadyAsync.
				if (_isMapReady && value is not null)
				{
					_ = _mapService.ApplyStyleAsync(value);
				}
			}
		}

		// ════════════════════════════════════════════════════════════════════════════════════════
		//  MAP MARKERS + USER LOCATION
		//  Self-contained block — the whole feature can be peeled out by deleting this region, the
		//  MapMarker/MarkerKind models, the map.js marker shims, and the "Selected Marker" tool
		//  window. Two concerns, kept separate on purpose:
		//    1. The "My Location" button: a transient locate ACTION + its in-progress/failure text
		//       (the Map card shows nothing persistent on success — see #2).
		//    2. The marker ENTITY: a MapMarker placed on the map, selectable + draggable, whose
		//       standing readout (coords + how its position was set) lives in the Selected Marker
		//       tool window. Drag updates flow JS → C# only (drag-only); manual coordinate entry /
		//       address search would add the C# → JS push (window.moveMarker) + a sync guard later.
		// ════════════════════════════════════════════════════════════════════════════════════════

		// Stable id shared with the JS marker (window.showUserLocation tags drag/click messages with it).
		private const string UserMarkerId = "user";

		// All markers currently on the map (today only the singleton user-location marker). A plain
		// list — nothing binds to it yet; the UI works off SelectedMarker. Promote to an observable
		// collection if/when a marker list view is added.
		private readonly List<MapMarker> _markers = new();

		// ── 1. Locate action (Map card) ──
		private bool _isLocating;
		private string _locateStatus = string.Empty; // transient only: "Locating…" / "Location unavailable"

		/// <summary>Whether a location resolve is in flight (drives the button spinner + disabled state).</summary>
		public bool IsLocating
		{
			get => _isLocating;
			private set
			{
				if (_isLocating == value)
				{
					return;
				}
				_isLocating = value;
				OnPropertyChanged();
				OnPropertyChanged(nameof(CanLocate));
			}
		}

		/// <summary>Inverse of <see cref="IsLocating"/> — the My Location button is enabled when idle.</summary>
		public bool CanLocate => !_isLocating;

		/// <summary>Transient locate status: "Locating…" while running, "Location unavailable" on
		/// failure, empty on success (the Selected Marker window owns the standing readout).</summary>
		public string LocateStatusText => _locateStatus;

		/// <summary>Whether there's transient locate text to show.</summary>
		public bool HasLocateStatus => _locateStatus.Length > 0;

		private void SetLocateStatus(string text)
		{
			if (_locateStatus == text)
			{
				return;
			}
			_locateStatus = text;
			OnPropertyChanged(nameof(LocateStatusText));
			OnPropertyChanged(nameof(HasLocateStatus));
		}

		/// <summary>
		/// Resolves the user's location (OS → IP fallback), drops/refreshes the singleton user-location
		/// marker, recenters on it, and selects it (so the Selected Marker window appears). No-op while
		/// already locating. On success the Map card status clears; the source readout is on the marker.
		/// </summary>
		public async Task ShowMyLocationAsync()
		{
			if (_isLocating)
			{
				return;
			}

			IsLocating = true;
			SetLocateStatus("Locating…");
			try
			{
				var location = await _locationService.ResolveAsync();
				if (location is null)
				{
					SetLocateStatus("Location unavailable");
					return;
				}

				var label = string.IsNullOrWhiteSpace(location.Description) ? "Your location" : location.Description!;
				var marker = UpsertUserLocationMarker(location.Latitude, location.Longitude, label, location.Source);

				if (_isMapReady)
				{
					// Call single-quotes the label without escaping; drop apostrophes (e.g. "Coeur d'Alene").
					await _mapService.ShowUserLocationAsync(location.Longitude, location.Latitude, label.Replace("'", string.Empty));
					await _mapService.FlyToAsync(location.Longitude, location.Latitude, 8);
				}

				SelectedMarker = marker;
				SetLocateStatus(string.Empty); // success: the Selected Marker window shows the result
			}
			finally
			{
				IsLocating = false;
			}
		}

		// Singleton enforcement lives here (not in the type): drop any existing user-location marker
		// and add the fresh one. Returns the new marker so the caller can select it.
		private MapMarker UpsertUserLocationMarker(double latitude, double longitude, string label, LocationSource source)
		{
			_markers.RemoveAll(m => m.Kind == MarkerKind.UserLocation);
			var marker = new MapMarker(UserMarkerId, MarkerKind.UserLocation, latitude, longitude, label,
				source, canDrag: true, isSingleton: true);
			_markers.Add(marker);
			return marker;
		}

		// ── 2. Marker entity (Selected Marker tool window) ──
		private MapMarker? _selectedMarker;

		/// <summary>The marker whose editor is shown (null = none). Set by a locate, a marker click,
		/// or cleared on remove.</summary>
		public MapMarker? SelectedMarker
		{
			get => _selectedMarker;
			private set
			{
				if (ReferenceEquals(_selectedMarker, value))
				{
					return;
				}
				_selectedMarker = value;
				OnPropertyChanged();
				OnPropertyChanged(nameof(HasSelectedMarker));
				RaiseSelectedMarker();
			}
		}

		/// <summary>Whether a marker is selected (drives the Selected Marker tool window's visibility).</summary>
		public bool HasSelectedMarker => _selectedMarker is not null;

		/// <summary>Editor title from the marker kind, e.g. "My Location".</summary>
		public string SelectedMarkerKindLabel => _selectedMarker?.Kind switch
		{
			MarkerKind.UserLocation => "My Location",
			_ => "Marker"
		};

		/// <summary>The marker's descriptive label (e.g. the resolved place name), or empty.</summary>
		public string SelectedMarkerSubtitle => _selectedMarker?.Label ?? string.Empty;

		/// <summary>The selected marker's coordinates, formatted (or empty).</summary>
		public string SelectedMarkerCoords => _selectedMarker is { } m
			? $"{m.Latitude:0.0000}, {m.Longitude:0.0000}"
			: string.Empty;

		/// <summary>How the selected marker's position was set — drives the editor's source icon/color.</summary>
		public LocationSource SelectedMarkerSource => _selectedMarker?.PositionSource ?? LocationSource.None;

		/// <summary>Friendly source label for the editor, e.g. "Device GPS" / "Manually adjusted".</summary>
		public string SelectedMarkerSourceText => _selectedMarker?.PositionSource switch
		{
			LocationSource.OperatingSystem => "Device GPS",
			LocationSource.IpAddress => "IP estimate (approximate)",
			LocationSource.Manual => "Manually adjusted",
			_ => string.Empty
		};

		/// <summary>Whether the selected marker can be dragged (shows the "drag to refine" hint).</summary>
		public bool CanDragSelectedMarker => _selectedMarker?.CanDrag ?? false;

		/// <summary>Whether the selected marker is the singleton user-location marker — drives the
		/// "Your location" badge + the re-detect/reset note in the editor.</summary>
		public bool SelectedMarkerIsUserLocation => _selectedMarker?.Kind == MarkerKind.UserLocation;

		private void RaiseSelectedMarker()
		{
			OnPropertyChanged(nameof(SelectedMarkerKindLabel));
			OnPropertyChanged(nameof(SelectedMarkerSubtitle));
			OnPropertyChanged(nameof(SelectedMarkerCoords));
			OnPropertyChanged(nameof(SelectedMarkerSource));
			OnPropertyChanged(nameof(SelectedMarkerSourceText));
			OnPropertyChanged(nameof(CanDragSelectedMarker));
			OnPropertyChanged(nameof(SelectedMarkerIsUserLocation));
		}

		/// <summary>A marker on the map was clicked (from JS): select it so its editor shows.</summary>
		public void OnMarkerClicked(string? id)
		{
			var marker = _markers.FirstOrDefault(m => m.Id == id);
			if (marker is not null)
			{
				SelectedMarker = marker;
			}
		}

		/// <summary>A marker was dragged (from JS): record the refined position and flag it manual.</summary>
		public void OnMarkerMoved(string? id, double longitude, double latitude)
		{
			var marker = _markers.FirstOrDefault(m => m.Id == id);
			if (marker is null)
			{
				return;
			}
			marker.Latitude = latitude;
			marker.Longitude = longitude;
			marker.PositionSource = LocationSource.Manual; // dragged → no longer the GPS/IP fix
			if (ReferenceEquals(marker, _selectedMarker))
			{
				RaiseSelectedMarker();
			}
		}

		/// <summary>Removes the selected marker from the map and the model, and clears the selection.</summary>
		public void RemoveSelectedMarker()
		{
			if (_selectedMarker is not { } marker)
			{
				return;
			}
			_markers.Remove(marker);
			if (marker.Kind == MarkerKind.UserLocation && _isMapReady)
			{
				_ = _mapService.ClearUserLocationAsync();
			}
			SelectedMarker = null;
		}

		// Appends one free-form radar diagnostic note. High-value events (session start, frame
		// timings, live polls, frame sources) use the typed RadarDiagnostics methods directly so
		// they also feed the rolling report; this is for the incidental lines.
		private static void Diag(string message) => Services.RadarDiagnostics.Log("vm", "note", ("msg", message));

		// Maps a loaded volume to its on-disk .V06 (the radarlevel2 host serves CacheDirectory),
		// so a suspect frame's source can be quarantined.
		private string FrameCacheFile(Models.RadarVolume v) =>
			System.IO.Path.Combine(_radarService.CacheDirectory, v.LocalUrl.Substring(v.LocalUrl.LastIndexOf('/') + 1));

		// Loads a fresh loop for the site (or clears for "None"): recenters immediately, shows
		// the newest frame first, then backfills older frames; also starts the playback and
		// auto-refresh loops tied to this selection. Cancels the previous selection's work.
		private async Task StartRadarLoopAsync(RadarSite? site)
		{
			Services.RadarDiagnostics.BeginSession(site?.Id);
			_loopCts?.Cancel();
			_loopCts = null;
			IsPlaying = false;
			SetLoopEngaged(false); // a freshly (re)loaded loop is stopped -> Stop disabled
			IsLoopReady = false;

			// Highlight the selected site marker (null clears it).
			if (_isMapReady)
			{
				await _mapService.SetSelectedRadarSiteAsync(site?.Id);
			}

			if (site is null)
			{
				ResetFrameState();
				OnPropertyChanged(nameof(MaxFrameIndex));
				OnPropertyChanged(nameof(CurrentFrameTimeText));
				RaiseRadarCard();
				IsInspecting = false; // no loop to inspect — drop the crosshair + marker
				if (_isMapReady)
				{
					await _mapService.ClearRadarAsync();
					await _mapService.SetRadarSweepAsync(0); // back to the free-running sweep
				}
				return;
			}

			// Start the load timer for this click (frozen once the initial load fully renders).
			_loopClickAt = DateTimeOffset.UtcNow;
			_firstFrameElapsed = null;
			_allFramesElapsed = null;
			_initialLoadDone = false;
			_loadInProgress = true;
			// Until THIS loop has actually begun (BeginRadarLoopAsync below, which bumps the JS
			// loop token), any frame-ready is a leftover from the previous selection still draining
			// through the worker — the JS token only drops stale frames AFTER the bump, so the gap
			// during the keys fetch leaks them. Ignore them so they don't pollute first-frame timing
			// or the ready count of the new session.
			_loopRenderBegun = false;
			_liveModeText = null; // forget the previous site's mode; the new site's poll re-sets it

			// Note: no flyTo — load the radar at the user's current view; they pan/zoom freely.
			var cts = new CancellationTokenSource();
			_loopCts = cts;

			await LoadLoopAsync(site, cts.Token);

			// The frame set (incl. any live frame) is now final; record "all frames" timing once
			// every frame has reported ready (or as they finish, via OnRadarFrameReady).
			_loadInProgress = false;
			MaybeRecordAllFramesLoaded();

			_ = RunPlaybackAsync(cts.Token);
			_ = RunRefreshAsync(site, cts.Token);
			_ = RunLiveFrameRefreshAsync(site, cts.Token);
			_ = RunDebugTickAsync(cts.Token);
		}

		// Zeroes all per-loop frame + live-poll state. Shared by the clear path (StartRadarLoopAsync)
		// and the Past Event Viewer's site-change clear (SelectPastSiteAsync). Callers raise the
		// relevant PropertyChanged / RaiseRadarCard afterwards.
		private void ResetFrameState()
		{
			_frameCount = 0;
			_archiveCount = 0;
			_hasLiveFrame = false;
			_liveFrame = null;
			_liveModeText = null;
			_frameTimes = Array.Empty<DateTimeOffset?>();
			_loadedNewestKey = null;
			_loadedKeys = Array.Empty<string>();
			_lastLivePollAt = null;
			_lastLivePollResult = null;
			_nextLivePollAt = null;
			_livePollCycleStart = null;
			_lastLiveError = null;
			_loopClickAt = null;
			_firstFrameElapsed = null;
			_allFramesElapsed = null;
		}

		/// <summary>
		/// Hard reset of the current loop: cancels the in-flight load/playback/refresh, dumps every
		/// frame, and reloads from scratch (re-list keys, re-decode, re-render — recovers a glitched
		/// loop without waiting for the ~5-min auto-refresh). No-op when no site is selected. Reuses
		/// the on-disk volume cache, so it's fast; it re-renders rather than re-downloading bytes.
		/// </summary>
		public void ResetRadarLoop()
		{
			if (_selectedRadarOption?.Site is not { } site)
			{
				return;
			}

			Diag("manual loop reset");
			_ = StartRadarLoopAsync(site);
		}

		// Past mode: a site pick clears any loaded replay and highlights the new site's marker, but
		// starts NOTHING — the user sets a window and hits Load. (Mirrors StartRadarLoopAsync's clear
		// path but keeps the marker on the chosen site so it reads as "armed" and runs no live loop.)
		private async Task SelectPastSiteAsync(RadarSite? site)
		{
			_loopCts?.Cancel();
			_loopCts = null;
			IsPlaying = false;
			SetLoopEngaged(false);
			IsLoopReady = false;
			IsInspecting = false;
			Services.RadarDiagnostics.BeginSession(null); // close any open session; Load opens the replay one
			ResetFrameState();
			OnPropertyChanged(nameof(MaxFrameIndex));
			OnPropertyChanged(nameof(CurrentFrameTimeText));
			RaiseRadarCard();
			if (_isMapReady)
			{
				await _mapService.SetSelectedRadarSiteAsync(site?.Id);
				await _mapService.ClearRadarAsync();
				await _mapService.SetRadarSweepAsync(0); // no live sweep in replay
			}
		}

		/// <summary>
		/// Loads the historical loop for the selected site over the chosen window (the Load button).
		/// Lists the archive volumes in the window, builds the loop with the same machinery as the
		/// live path, then starts playback — but with NO live poll and NO auto-refresh.
		/// </summary>
		public async Task LoadSelectedPastEventAsync()
		{
			if (!_isPastEventMode)
			{
				return;
			}
			if (_selectedRadarOption?.Site is not { } site)
			{
				PastEventStatus = "Select a radar site in the Radar Sites list first.";
				return;
			}

			// Build the local date from the Year/Month/Day combos; clamp the day to the month's length
			// (so e.g. day 31 in a 30-day month just uses the 30th instead of throwing). Combine with
			// the time-of-day, then convert to UTC for the bucket query (DST-correct for that date).
			var year = PastEventYearOptions[_pastEventYearIndex];
			var month = _pastEventMonthIndex + 1;
			var day = Math.Min(_pastEventDayIndex + 1, DateTime.DaysInMonth(year, month));
			var localMidnight = new DateTimeOffset(year, month, day, 0, 0, 0,
				TimeZoneInfo.Local.GetUtcOffset(new DateTime(year, month, day)));
			var localStart = localMidnight + _pastEventTime;
			var startUtc = localStart.ToUniversalTime();
			var endUtc = startUtc.AddMinutes(PastEventMinutesByIndex[_pastEventDurationIndex]);

			PastEventStatus = "Loading…";
			_loopCts?.Cancel();
			var cts = new CancellationTokenSource();
			_loopCts = cts;

			IReadOnlyList<string> keys;
			try
			{
				keys = await _radarService.GetKeysForWindowAsync(site, startUtc, endUtc, cts.Token);
			}
			catch (OperationCanceledException)
			{
				return;
			}
			catch (Exception ex)
			{
				PastEventStatus = "Couldn't list volumes: " + ex.Message;
				return;
			}

			if (cts.Token.IsCancellationRequested || !ReferenceEquals(_selectedRadarOption?.Site, site))
			{
				return;
			}
			if (keys.Count == 0)
			{
				PastEventStatus = $"No {site.Id} data found for {localStart:MMM d, h:mm tt}.";
				return;
			}
			// More volumes than the cap → evenly subsample across the whole window (first + last kept),
			// so a long duration becomes an overview rather than only the first chunk.
			var sampled = false;
			if (keys.Count > PastEventMaxFrames)
			{
				var pick = new List<string>(PastEventMaxFrames);
				for (var i = 0; i < PastEventMaxFrames; i++)
				{
					var idx = (int)Math.Round((double)i * (keys.Count - 1) / (PastEventMaxFrames - 1));
					pick.Add(keys[idx]);
				}
				keys = pick.Distinct().ToList();
				sampled = true;
			}

			await LoadPastLoopAsync(site, keys, startUtc, cts.Token);
			if (cts.Token.IsCancellationRequested)
			{
				return;
			}

			MaybeRecordAllFramesLoaded();
			_ = RunPlaybackAsync(cts.Token);
			_ = RunDebugTickAsync(cts.Token);

			PastEventStatus = $"Loaded {keys.Count} frames{(sampled ? " (sampled)" : "")} · " +
				$"{localStart:MMM d, h:mm tt} +{PastEventDurationOptions[_pastEventDurationIndex]}";
		}

		// Builds the replay loop from the given keys — the live-free counterpart of LoadLoopCoreAsync.
		private async Task LoadPastLoopAsync(RadarSite site, IReadOnlyList<string> keys, DateTimeOffset startUtc, CancellationToken ct)
		{
			try
			{
				await _loopGate.WaitAsync(ct);
			}
			catch (OperationCanceledException)
			{
				return;
			}

			try
			{
				Services.RadarDiagnostics.BeginSession(site.Id);
				Services.RadarDiagnostics.Log("vm", "replay.load",
					("site", site.Id), ("startZ", startUtc.ToString("O")), ("frames", keys.Count));

				_loopClickAt = DateTimeOffset.UtcNow;
				_firstFrameElapsed = null;
				_allFramesElapsed = null;
				_initialLoadDone = false;
				_loadInProgress = true;
				_loopRenderBegun = false;

				_archiveCount = keys.Count;
				_frameCount = keys.Count;
				_liveFrame = null;
				_hasLiveFrame = false;
				_liveModeText = null;
				_frameTimes = new DateTimeOffset?[_frameCount];
				_readyCount = 0;
				_loadedKeys = keys.ToArray();
				_loadedNewestKey = keys[^1];
				IsLoopReady = false;
				_currentFrameIndex = 0; // start at the beginning of the event so play moves forward
				OnPropertyChanged(nameof(MaxFrameIndex));
				OnPropertyChanged(nameof(CurrentFrameIndex));
				OnPropertyChanged(nameof(CurrentFrameTimeText));

				if (_isMapReady)
				{
					await _mapService.BeginRadarLoopAsync(site);
				}
				_loopRenderBegun = true;

				// Oldest frame first (it's adopted + shown immediately), then the rest in order.
				await EnsureAndAddFrameAsync(site, keys, 0, ct);
				for (var i = 1; i < keys.Count && !ct.IsCancellationRequested; i++)
				{
					await EnsureAndAddFrameAsync(site, keys, i, ct);
				}
				_loadInProgress = false;
			}
			finally
			{
				_loopGate.Release();
			}
		}

		// Ticks the debug card once a second so its ages stay current while a loop is active.
		private async Task RunDebugTickAsync(CancellationToken ct)
		{
			try
			{
				using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
				while (await timer.WaitForNextTickAsync(ct))
				{
					RaiseRadarCard();
				}
			}
			catch (OperationCanceledException)
			{
				// Selection changed or app shutting down.
			}
		}

		// Lists the recent archive volumes, fetches the near-real-time live frame from the
		// chunks bucket, begins a loop, shows the newest, then backfills the rest. The live
		// frame (when available) is appended as an extra newest frame at index _archiveCount.
		private async Task LoadLoopAsync(RadarSite site, CancellationToken ct)
		{
			try
			{
				await _loopGate.WaitAsync(ct);
			}
			catch (OperationCanceledException)
			{
				return;
			}

			try
			{
				await LoadLoopCoreAsync(site, ct);
			}
			finally
			{
				_loopGate.Release();
			}
		}

		// The actual load, run under _loopGate (see LoadLoopAsync). Ends by fetching the live
		// frame inline so the whole sequence is atomic w.r.t. the live poll.
		private async Task LoadLoopCoreAsync(RadarSite site, CancellationToken ct)
		{
			IReadOnlyList<string> keys;
			try
			{
				keys = await _radarService.GetRecentKeysAsync(site, LoopLength, ct);
			}
			catch (OperationCanceledException)
			{
				return;
			}
			catch
			{
				return;
			}

			if (ct.IsCancellationRequested || keys.Count == 0 || !ReferenceEquals(_selectedRadarOption?.Site, site))
			{
				Services.RadarDiagnostics.Log("vm", "loop.abort", ("keys", keys.Count), ("cancelled", ct.IsCancellationRequested));
				return;
			}

			Services.RadarDiagnostics.Log("vm", "loop.keys", ("count", keys.Count), ("newest", keys[^1]));

			// Size the loop for the archive frames only; the live frame (if it turns out to be
			// fresher) is appended afterwards by RefreshLiveFrameAsync. Loading archive first
			// also means the newest archive frame paints immediately, before the chunks fetch.
			_archiveCount = keys.Count;
			_liveFrame = null;
			_hasLiveFrame = false;
			_frameCount = _archiveCount;
			_frameTimes = new DateTimeOffset?[_frameCount];
			_readyCount = 0;
			_loadedNewestKey = keys[_archiveCount - 1]; // archive newest drives the 5-min reload
			_loadedKeys = keys.ToArray();               // baseline for the next incremental refresh
			IsLoopReady = false;
			_currentFrameIndex = _frameCount - 1; // newest archive frame
			OnPropertyChanged(nameof(MaxFrameIndex));
			OnPropertyChanged(nameof(CurrentFrameIndex));
			OnPropertyChanged(nameof(CurrentFrameTimeText));

			if (_isMapReady)
			{
				await _mapService.BeginRadarLoopAsync(site);
			}

			// The loop (and its JS token) is now (re)started — frame-ready events from here on
			// belong to this selection, so first-frame timing can trust them.
			_loopRenderBegun = true;

			// Newest archive frame first (immediate display).
			await EnsureAndAddFrameAsync(site, keys, _archiveCount - 1, ct);

			// Pull the live (chunks) frame + scan mode NEXT — before the older-frame backfill — so
			// the card's time/mode/freshness populate in ~1-2 s instead of waiting out the full
			// backfill (~5 s). It appends at index _archiveCount when fresher; the backfill then
			// fills 0.._archiveCount-2 behind the already-populated card.
			await RefreshLiveFrameAsync(site, ct);

			// Backfill the older archive frames. Bound by _archiveCount, NOT _frameCount — the live
			// frame above may have grown _frameCount, and these indices are archive-only.
			for (var i = 0; i < _archiveCount - 1 && !ct.IsCancellationRequested; i++)
			{
				await EnsureAndAddFrameAsync(site, keys, i, ct);
			}
		}

		// Fetches the live (chunks) frame and, when it's newer than what's shown, applies it —
		// appending a new trailing frame or updating the existing live slot in place. Records the
		// outcome for the debug card. Best-effort: a null result just leaves the archive newest.
		private async Task RefreshLiveFrameAsync(RadarSite site, CancellationToken ct)
		{
			Models.RadarVolume? live;
			try
			{
				_lastLiveError = null;
				live = await _radarService.GetLiveFrameAsync(site, ct);
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				_lastLiveError = ex.Message;
				live = null;
			}

			RecordLivePoll(live);

			if (live is null || ct.IsCancellationRequested || !ReferenceEquals(_selectedRadarOption?.Site, site))
			{
				return;
			}

			await ApplyLiveFrameAsync(live);
		}

		// Applies a live frame: updates the existing trailing live slot in place, or appends a new
		// one — but only when strictly newer than the current live / archive newest, so a stale
		// chunks volume can never override fresh data (the bug behind KVNX/KFDR old frames).
		private async Task ApplyLiveFrameAsync(Models.RadarVolume live)
		{
			if (_hasLiveFrame)
			{
				if (_liveFrame is not null && live.VolumeTime <= _liveFrame.VolumeTime)
				{
					Services.RadarDiagnostics.Log("vm", "live.apply", ("action", "skip"),
						("reason", $"not newer than current live ({live.VolumeTime:HH:mm:ss}Z <= {_liveFrame.VolumeTime:HH:mm:ss}Z)"));
					return;
				}

				_liveFrame = live;
				if (_archiveCount < _frameTimes.Length)
				{
					_frameTimes[_archiveCount] = live.VolumeTime;
				}
				Services.RadarDiagnostics.Log("vm", "live.apply", ("action", "update"),
					("idx", _archiveCount), ("volZ", live.VolumeTime.ToUniversalTime().ToString("HH:mm:ss")));
				Services.RadarDiagnostics.RegisterFrameSource(_archiveCount, "live", FrameCacheFile(live), live.VolumeTime);
				if (_isMapReady)
				{
					await _mapService.AddRadarFrameAsync(live.LocalUrl, _archiveCount);
				}
				if (_currentFrameIndex == _archiveCount)
				{
					OnPropertyChanged(nameof(CurrentFrameTimeText));
				}
				RaiseRadarCard();
				return;
			}

			// No live slot yet: only append if the chunks volume is newer than the archive newest.
			var archiveNewest = _archiveCount > 0 && _archiveCount - 1 < _frameTimes.Length
				? _frameTimes[_archiveCount - 1]
				: null;
			if (archiveNewest is { } an && live.VolumeTime <= an)
			{
				Services.RadarDiagnostics.Log("vm", "live.apply", ("action", "skip"),
					("reason", $"not newer than archive ({live.VolumeTime:HH:mm:ss}Z <= {an:HH:mm:ss}Z)"));
				return;
			}

			var grown = new DateTimeOffset?[_archiveCount + 1];
			Array.Copy(_frameTimes, grown, _archiveCount);
			grown[_archiveCount] = live.VolumeTime;
			_frameTimes = grown;
			_liveFrame = live;
			_hasLiveFrame = true;
			_frameCount = _archiveCount + 1;
			_currentFrameIndex = _frameCount - 1; // show the live frame as the new newest
			Services.RadarDiagnostics.Log("vm", "live.apply", ("action", "append"),
				("idx", _archiveCount), ("volZ", live.VolumeTime.ToUniversalTime().ToString("HH:mm:ss")),
				("frames", _frameCount));
			Services.RadarDiagnostics.RegisterFrameSource(_archiveCount, "live", FrameCacheFile(live), live.VolumeTime);
			if (_loopClickAt is { } liveClick)
			{
				Services.RadarDiagnostics.Timing("live", (DateTimeOffset.UtcNow - liveClick).TotalSeconds);
			}
			OnPropertyChanged(nameof(MaxFrameIndex));
			OnPropertyChanged(nameof(CurrentFrameIndex));
			OnPropertyChanged(nameof(CurrentFrameTimeText));
			RaiseRadarCard();

			if (_isMapReady)
			{
				await _mapService.AddRadarFrameAsync(live.LocalUrl, _archiveCount);
				await _mapService.ShowRadarFrameAsync(_archiveCount);
			}
		}

		// Records the latest live-frame poll outcome for the debug card.
		private void RecordLivePoll(Models.RadarVolume? live)
		{
			_lastLivePollAt = DateTimeOffset.Now;
			_lastLivePollResult = _lastLiveError is not null
				? $"error: {_lastLiveError}"
				: live is null
					? "null (no fresh tilt; using archive)"
					: $"ok · {live.VolumeTime.ToUniversalTime():HH:mm:ss}Z";
			// The decoded volume carries the scan mode regardless of whether it's fresh enough to
			// append as a new frame — capture it so the mode shows even for a stale/offline site.
			if (live?.ModeText is { } mode)
			{
				_liveModeText = mode;
			}
			Services.RadarDiagnostics.LivePoll(_lastLivePollResult, live?.VolumeTime, live?.ModeText);
			RaiseRadarCard();
		}

		private async Task EnsureAndAddFrameAsync(RadarSite site, IReadOnlyList<string> keys, int index, CancellationToken ct)
		{
			try
			{
				var volume = await _radarService.EnsureCachedAsync(site, keys[index], ct);
				if (volume is null || ct.IsCancellationRequested || !ReferenceEquals(_selectedRadarOption?.Site, site))
				{
					return;
				}

				_frameTimes[index] = volume.VolumeTime;
				Services.RadarDiagnostics.RegisterFrameSource(index, "archive", FrameCacheFile(volume), volume.VolumeTime);
				if (_isMapReady)
				{
					await _mapService.AddRadarFrameAsync(volume.LocalUrl, index);
				}
			}
			catch (OperationCanceledException)
			{
				// Selection changed; stop.
			}
			catch (Exception ex)
			{
				// Skip a bad frame; the rest of the loop still loads.
				Services.RadarDiagnostics.Log("vm", "frame.fail", ("idx", index), ("error", ex.Message));
			}
		}

		/// <summary>Called by the view when a radar site marker is clicked (toggles selection).</summary>
		public void OnRadarSiteClicked(string? id)
		{
			if (string.IsNullOrEmpty(id))
			{
				return;
			}

			var option = RadarOptions.FirstOrDefault(o => o.Site?.Id == id);
			if (option is null)
			{
				return;
			}

			// Clicking the already-selected site clears it; otherwise select it.
			SelectedRadarOption = ReferenceEquals(option, _selectedRadarOption) ? RadarOptions[0] : option;
		}

		/// <summary>Called by the view when the WebView reports a loop frame finished decoding.</summary>
		public void OnRadarFrameReady(int index, bool hasData)
		{
			// Drop frame-ready events that arrive before this selection's loop has begun — they're
			// stale leftovers from the previous site draining through the worker (see _loopRenderBegun).
			if (!_loopRenderBegun)
			{
				return;
			}

			_readyCount++;
			Services.RadarDiagnostics.FrameReady(index, hasData, _readyCount, _frameCount);

			// First-frame timing: the moment the first frame of this click is decoded + shown.
			if (!_initialLoadDone && _firstFrameElapsed is null && _loopClickAt is { } click)
			{
				_firstFrameElapsed = DateTimeOffset.UtcNow - click;
				Services.RadarDiagnostics.Timing("first", _firstFrameElapsed.Value.TotalSeconds);
				RaiseRadarCard();
			}

			OnPropertyChanged(nameof(CurrentFrameTimeText));
			if (_readyCount >= _frameCount && _frameCount > 0)
			{
				IsLoopReady = true;
				OnPropertyChanged(nameof(CurrentFrameTimeText));
			}

			MaybeRecordAllFramesLoaded();
		}

		// Records the "all frames loaded + rendered" timing once, after the initial load has
		// settled on its final frame count (so the live frame is included) and every frame has
		// reported ready. Frozen by _initialLoadDone so later live refreshes don't overwrite it.
		private void MaybeRecordAllFramesLoaded()
		{
			if (_initialLoadDone || _loadInProgress || _frameCount == 0 || _readyCount < _frameCount)
			{
				return;
			}
			if (_loopClickAt is { } click)
			{
				_allFramesElapsed = DateTimeOffset.UtcNow - click;
				Services.RadarDiagnostics.Timing("all", _allFramesElapsed.Value.TotalSeconds);
				RaiseRadarCard();
			}
			_initialLoadDone = true;
		}

		// Advances the loop while playing + ready (~0.5s/frame, with a brief dwell on newest).
		private async Task RunPlaybackAsync(CancellationToken ct)
		{
			try
			{
				var dwell = 0;
				while (!ct.IsCancellationRequested)
				{
					// Variable per-frame delay so the playback-speed combo applies immediately.
					await Task.Delay(PlaybackIntervalMs, ct);
					if (!_isPlaying || !_isLoopReady || _frameCount == 0)
					{
						continue;
					}

					// Pause a couple of ticks on the newest frame before looping back.
					if (_currentFrameIndex >= _frameCount - 1 && dwell < 2)
					{
						dwell++;
						continue;
					}

					dwell = 0;
					CurrentFrameIndex = (_currentFrameIndex + 1) % _frameCount;
				}
			}
			catch (OperationCanceledException)
			{
				// Selection changed or app shutting down.
			}
		}

		// Every ~5 min, if a newer volume exists, reloads the loop (keeps the hour current).
		private async Task RunRefreshAsync(RadarSite site, CancellationToken ct)
		{
			try
			{
				using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
				while (await timer.WaitForNextTickAsync(ct))
				{
					if (!ReferenceEquals(_selectedRadarOption?.Site, site))
					{
						return;
					}

					IReadOnlyList<string> keys;
					try
					{
						keys = await _radarService.GetRecentKeysAsync(site, LoopLength, ct);
					}
					catch (OperationCanceledException)
					{
						return;
					}
					catch
					{
						continue;
					}

					if (keys.Count > 0 && keys[keys.Count - 1] != _loadedNewestKey)
					{
						Services.RadarDiagnostics.Log("vm", "refresh.archive", ("newKey", keys[^1]), ("oldKey", _loadedNewestKey));
						// Incrementally fold in the new volume (reuse the unchanged decoded frames, no
						// layer teardown) instead of a full rebuild — that rebuild blanked the radar for
						// ~1.5-6 s and flashed a stale archive frame every 5 min.
						await ReloadLoopIncrementalAsync(site, keys, ct);
					}
				}
			}
			catch (OperationCanceledException)
			{
				// Selection changed or app shutting down.
			}
		}

		// Folds a newly-arrived archive volume into the loop WITHOUT a teardown: it diffs the new key
		// list against the loaded one, reindexes (in JS) the frames whose volumes are unchanged so
		// their decoded geometry is reused, and decodes only the genuinely-new volume(s). The live
		// frame is carried over too. Because the layer is never removed and the on-screen frame stays
		// up, the periodic reload no longer blanks the radar or flashes a stale archive frame.
		// Serialized under _loopGate against the live poll, like the full load.
		private async Task ReloadLoopIncrementalAsync(RadarSite site, IReadOnlyList<string> newKeys, CancellationToken ct)
		{
			try
			{
				await _loopGate.WaitAsync(ct);
			}
			catch (OperationCanceledException)
			{
				return;
			}

			try
			{
				if (newKeys.Count == 0 || !ReferenceEquals(_selectedRadarOption?.Site, site))
				{
					return;
				}

				var oldFrameTimes = _frameTimes;
				var oldArchiveCount = _archiveCount;
				var oldFrameCount = _frameCount;
				var hadLive = _hasLiveFrame;
				var oldCurrent = _currentFrameIndex;
				var wasNewest = oldCurrent == oldFrameCount - 1; // user following the latest frame

				// First old index for each archive key (the loop has no duplicate volumes in practice).
				var oldIndexByKey = new Dictionary<string, int>(oldArchiveCount);
				for (var i = 0; i < _loadedKeys.Length && i < oldArchiveCount; i++)
				{
					oldIndexByKey.TryAdd(_loadedKeys[i], i);
				}

				var newArchiveCount = newKeys.Count;
				var newFrameCount = newArchiveCount + (hadLive ? 1 : 0);
				var newTimes = new DateTimeOffset?[newFrameCount];
				var mapping = new List<int[]>(newFrameCount);     // [fromIndex, toIndex] reuses
				var newIndices = new List<int>();                  // new archive slots needing decode

				for (var j = 0; j < newArchiveCount; j++)
				{
					if (oldIndexByKey.TryGetValue(newKeys[j], out var oi) && oi < oldFrameTimes.Length)
					{
						mapping.Add(new[] { oi, j });
						newTimes[j] = oldFrameTimes[oi];
					}
					else
					{
						newIndices.Add(j); // brand-new volume -> decode
					}
				}

				// The live (chunks) frame persists across an archive reload — carry it to the new top.
				if (hadLive && oldArchiveCount < oldFrameTimes.Length)
				{
					mapping.Add(new[] { oldArchiveCount, newArchiveCount });
					newTimes[newArchiveCount] = oldFrameTimes[oldArchiveCount];
				}

				// Where the displayed frame lands after the reindex (so we can keep it on screen).
				var newCurrent = -1;
				foreach (var m in mapping)
				{
					if (m[0] == oldCurrent) { newCurrent = m[1]; break; }
				}

				// Commit VM state. Don't touch IsLoopReady: most frames are already decoded, so the
				// loop stays "ready" (scrubber/playback uninterrupted). _readyCount = reused count;
				// the new frames bring it back up to newFrameCount as they decode.
				_loadedKeys = newKeys.ToArray();
				_loadedNewestKey = newKeys[^1];
				_archiveCount = newArchiveCount;
				_frameCount = newFrameCount;
				_frameTimes = newTimes;
				_readyCount = mapping.Count;
				_currentFrameIndex = wasNewest ? newFrameCount - 1
					: newCurrent >= 0 ? newCurrent
					: newFrameCount - 1;

				Services.RadarDiagnostics.Log("vm", "refresh.incremental",
					("reused", mapping.Count), ("new", newIndices.Count),
					("frames", newFrameCount), ("newest", newKeys[^1]));

				if (_isMapReady)
				{
					var mappingJson = System.Text.Json.JsonSerializer.Serialize(mapping);
					await _mapService.RemapRadarFramesAsync(newFrameCount, mappingJson);
					// Target the desired frame: if undecoded (a just-arrived newest), JS records it as
					// pending and keeps the current frame on screen until it decodes (no blank).
					await _mapService.ShowRadarFrameAsync(_currentFrameIndex);
				}

				OnPropertyChanged(nameof(MaxFrameIndex));
				OnPropertyChanged(nameof(CurrentFrameIndex));
				OnPropertyChanged(nameof(CurrentFrameTimeText));
				RaiseRadarCard();

				// Decode only the genuinely-new volumes (newest-first so the top updates first).
				for (var k = newIndices.Count - 1; k >= 0 && !ct.IsCancellationRequested; k--)
				{
					await EnsureAndAddFrameAsync(site, newKeys, newIndices[k], ct);
				}
			}
			finally
			{
				_loopGate.Release();
			}
		}

		// Pulls the freshest chunks-bucket frame and (when newer) updates the trailing live slot
		// in place — or appends one if the loop didn't have a live frame yet — keeping the newest
		// frame ~1-2 min old between the slower (~5 min) archive reloads. Polls fast until the
		// first live frame lands (LiveFrameRetrySeconds), then settles to LiveFrameRefreshSeconds.
		private async Task RunLiveFrameRefreshAsync(RadarSite site, CancellationToken ct)
		{
			try
			{
				while (true)
				{
					var interval = _hasLiveFrame ? RefreshIntervalSeconds : LiveFrameRetrySeconds;
					// Schedule relative to the LAST poll, whoever ran it — an archive reload runs its
					// own inline live poll (LoadLoopCoreAsync → RefreshLiveFrameAsync), so anchoring on
					// _lastLivePollAt pushes this timer out instead of double-fetching ~3s later.
					var sinceLast = _lastLivePollAt is { } last ? (DateTimeOffset.Now - last).TotalSeconds : interval;
					var wait = Math.Max(1.0, interval - sinceLast);
					_livePollCycleStart = DateTimeOffset.Now;
					_nextLivePollAt = _livePollCycleStart.Value.AddSeconds(wait);
					OnPropertyChanged(nameof(RadarNextFrameProgress)); // reset the bar at the cycle start
					// Phase-lock the on-map radar sweep to this cycle: one revolution == the time
					// until the next live poll, so the arm completes as the next update is due.
					_ = _mapService.SetRadarSweepAsync(wait);
					await Task.Delay(TimeSpan.FromSeconds(wait), ct);

					if (!ReferenceEquals(_selectedRadarOption?.Site, site))
					{
						return;
					}

					// If a poll snuck in during our wait (e.g. a reload's inline poll), don't double
					// up — loop to recompute the next deadline from that poll instead.
					if (_lastLivePollAt is { } recent && (DateTimeOffset.Now - recent).TotalSeconds < interval - 1)
					{
						continue;
					}

					// Gate against a concurrent archive (re)load mutating the same frame state.
					await _loopGate.WaitAsync(ct);
					try
					{
						if (ReferenceEquals(_selectedRadarOption?.Site, site))
						{
							await RefreshLiveFrameAsync(site, ct);
						}
					}
					finally
					{
						_loopGate.Release();
					}
				}
			}
			catch (OperationCanceledException)
			{
				// Selection changed or app shutting down.
			}
		}

		/// <summary>
		/// Called by the view once the map page has fired its 'load' event. Enables live
		/// style switching and shows the initially-selected outlook.
		/// </summary>
		public async Task OnMapsReadyAsync()
		{
			_isMapReady = true;

			// The page loaded the selected style via its URL; re-apply it to pick up any
			// change made before the map was ready (idempotent when unchanged).
			if (_selectedStyle is not null)
			{
				await _mapService.ApplyStyleAsync(_selectedStyle);
			}

			// Show the selected outlook only if the visibility toggle is on (it defaults off, so
			// the app launches with no outlook); sync the fill opacity to the slider's initial value.
			var startupProduct = _selectedOption?.Product;
			if (startupProduct is not null && _isOutlookVisible)
			{
				await _mapService.ShowOutlookAsync(startupProduct);
			}
			await _mapService.SetOutlookOpacityAsync(_outlookOpacity);
			UpdateOutlookTimes();

			// Point the page at the cached SPC watch boxes and apply the current toggle state
			// (default off, so nothing draws on launch; a background refresh keeps it fresh).
			await _mapService.SetWatchSourceAsync(_watchService.WatchesUrl);
			await _mapService.SetWatchesVisibleAsync(_showWatches);

			// Provide the radar sites as clickable on-map markers.
			var sites = _radarSiteProvider.GetSites()
				.Select(s => new { id = s.Id, name = s.Name, lng = s.Longitude, lat = s.Latitude });
			await _mapService.ShowRadarSitesAsync(System.Text.Json.JsonSerializer.Serialize(sites));

			// Flag sites with no recent data ("offline") and keep that refreshed.
			_ = RunSiteStatusLoopAsync();

			// Drive the next-update progress bars (radar live frame + SPC outlook).
			_ = RunProgressTickAsync();
		}

		// Periodically marks which site markers are offline (no data in the feed) so a feed
		// outage like KLIX reads as "offline", not a broken app. Sites change state on hour
		// scales, so a ~10-min refresh is plenty. Runs for the app lifetime (best-effort).
		private async Task RunSiteStatusLoopAsync()
		{
			while (true)
			{
				try
				{
					var live = await _radarService.GetLiveSiteIdsAsync();
					var offline = _radarSiteProvider.GetSites()
						.Select(s => s.Id)
						.Where(id => !live.Contains(id))
						.ToList();
					// Reflect the same offline set in the dock list rows (down = red, not clickable).
					// These awaits resume on the UI thread, so updating the observable rows is safe.
					var offlineSet = new HashSet<string>(offline);
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
				catch
				{
					// Best effort: a failed status check just leaves the markers as they are.
				}

				await Task.Delay(TimeSpan.FromMinutes(10));
			}
		}

		public event PropertyChangedEventHandler? PropertyChanged;

		private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
		{
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
		}
	}
}
