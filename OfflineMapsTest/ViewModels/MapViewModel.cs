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
		private readonly IRadarSiteProvider _radarSiteProvider;
		private readonly ILevel2RadarService _radarService;

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
		private const int LoopFrameCount = 10;
		private DateTimeOffset?[] _frameTimes = Array.Empty<DateTimeOffset?>();
		private int _frameCount;
		private int _readyCount;
		private int _currentFrameIndex;
		private bool _isPlaying;
		private bool _isLoopReady;
		private string? _loadedNewestKey;
		private CancellationTokenSource? _loopCts;

		// Serializes loop mutation so the archive (re)load and the live-frame poll can't
		// interleave at await points and corrupt the frame arrays / VM↔JS index state.
		private readonly System.Threading.SemaphoreSlim _loopGate = new(1, 1);

		// Near-real-time "live" frame from the chunks bucket, appended as an extra newest frame
		// (index _archiveCount) on top of the archive-bucket history. _hasLiveFrame says whether
		// that slot exists; _liveFrame holds the current one (its time gates in-place updates).
		// A faster poll (RunLiveFrameRefreshAsync) keeps it fresh between archive reloads; 30s
		// catches each new SAILS 0.5° re-scan (~every 1.5-3 min) soon after it finishes without
		// much wasted traffic (clear-air VCPs only scan ~every 10 min, the real floor there).
		private const double LiveFrameRefreshSeconds = 30;
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
		// wait), so the debug card can show a countdown to it.
		private DateTimeOffset? _nextLivePollAt;

		// Load timing for the current selection: from the site click to the first frame ready,
		// and to ALL frames (final count, incl. the live frame) ready+rendered. Captured once per
		// click (frozen after the initial load, so the ~60s live refreshes don't overwrite them).
		private DateTimeOffset? _loopClickAt;
		private TimeSpan? _firstFrameElapsed;
		private TimeSpan? _allFramesElapsed;
		private bool _initialLoadDone;
		private bool _loadInProgress;

		public MapViewModel(IMapService mapService, IStyleProvider styleProvider, IRegionProvider regionProvider, ISpcOutlookService spcOutlookService, IRadarSiteProvider radarSiteProvider, ILevel2RadarService radarService)
		{
			_mapService = mapService;
			_styleProvider = styleProvider;
			_regionProvider = regionProvider;
			_spcOutlookService = spcOutlookService;
			_radarSiteProvider = radarSiteProvider;
			_radarService = radarService;

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

			// SPC outlook selectors. Default to Day 1 Categorical so an outlook is
			// visible on launch; assign backing fields directly so construction fires no
			// map command (OnMapsReadyAsync shows the default once the map is ready).
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

			// Flat site list for the dock's "Radar Sites" tool window (click a row to select).
			RadarSites = _radarSiteProvider.GetSites();
		}

		/// <summary>All radar sites (id/name/coords), for the dock's "Radar Sites" tool-window list.</summary>
		public IReadOnlyList<RadarSite> RadarSites { get; }

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

		/// <summary>Whether an outlook (not "None") is selected — drives the info card's visibility.</summary>
		public bool HasOutlookCard => _selectedOption?.Product is not null;

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
				OnPropertyChanged(nameof(HasRadarLoop));
				RaiseRadarCard();
				_ = StartRadarLoopAsync(value?.Site);
			}
		}

		/// <summary>Whether a radar site is selected (drives the loop controls' visibility).</summary>
		public bool HasRadarLoop => _selectedRadarOption?.Site is not null;

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

		/// <summary>
		/// Developer diagnostics for the on-map radar card (lower-left): everything about the
		/// current loop and the live (chunks) frame — times, ages, per-frame source, loop state,
		/// and the last live-poll outcome. Recomputed on frame changes and on a 1s tick so ages
		/// stay current.
		/// </summary>
		public string RadarDebugText
		{
			get
			{
				var site = _selectedRadarOption?.Site;
				if (site is null)
				{
					return string.Empty;
				}

				var now = DateTimeOffset.Now;
				static string Z(DateTimeOffset? t) => t is { } x ? x.ToUniversalTime().ToString("HH:mm:ss") + "Z" : "—";
				string Age(DateTimeOffset? t) => t is { } x ? $"{(now - x).TotalMinutes:0.0}m" : "—";

				var cur = (_currentFrameIndex >= 0 && _currentFrameIndex < _frameTimes.Length) ? _frameTimes[_currentFrameIndex] : null;
				var curSrc = (_hasLiveFrame && _currentFrameIndex == _archiveCount) ? "LIVE" : "ARCHIVE";
				var liveT = (_hasLiveFrame && _archiveCount < _frameTimes.Length) ? _frameTimes[_archiveCount] : null;
				var archNewest = (_archiveCount > 0 && _archiveCount - 1 < _frameTimes.Length) ? _frameTimes[_archiveCount - 1] : null;
				var oldest = _frameTimes.Length > 0 ? _frameTimes[0] : null;

				var state = _isLoopReady ? "ready" : $"loading {_readyCount}/{_frameCount}";
				var pollAge = _lastLivePollAt is { } p ? $"{(now - p).TotalSeconds:0}s ago" : "—";
				var nextPoll = _nextLivePollAt is { } np
					? $"next in {Math.Max(0, (np - now).TotalSeconds):0}s"
					: "next —";

				var firstT = _firstFrameElapsed is { } ff ? $"{ff.TotalSeconds:0.0}s" : "…";
				var allT = _allFramesElapsed is { } af ? $"{af.TotalSeconds:0.0}s" : "…";

				// Fixed-width label column so values line up (monospace card font).
				static string Row(string label, string value) => $"{label,-9}{value}";

				var sb = new System.Text.StringBuilder();
				sb.AppendLine($"RADAR DEBUG   {site.Id}  {site.Name}");
				sb.AppendLine($"{site.Latitude:0.000}, {site.Longitude:0.000}      now {now.ToUniversalTime():HH:mm:ss}Z");
				sb.AppendLine("──────────────────────────────────────");
				sb.AppendLine(Row("frame", $"#{_currentFrameIndex + 1}/{_frameCount}   [{curSrc}]   live slot {(_hasLiveFrame ? "yes" : "no")}   {state}{(_isPlaying ? " · playing" : "")}"));
				sb.AppendLine(Row("mode", _liveModeText ?? "— (awaiting live frame)"));
				sb.AppendLine(Row("load", $"first {firstT}     all {allT}"));
				sb.AppendLine(Row("current", $"{Z(cur)}     age {Age(cur)}"));
				sb.AppendLine(Row("live", _liveFrame is null
					? "none yet (see poll line)"
					: $"{Z(liveT)}     age {Age(liveT)}"));
				sb.AppendLine(Row("archive", $"{Z(archNewest)}     age {Age(archNewest)}"));
				sb.AppendLine(Row("span", $"{Z(oldest)} → {Z(archNewest)}   ({_archiveCount} frames)"));
				sb.AppendLine(Row("poll", $"{_lastLivePollResult ?? "—"}   ({pollAge} · {nextPoll})"));
				sb.AppendLine($"─── log ({Services.RadarDebugLog.TotalCount} events) ──────────────");
				foreach (var line in Services.RadarDebugLog.Tail(8))
				{
					sb.AppendLine(line);
				}
				return sb.ToString().TrimEnd();
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

		// Raises the polished card properties + the diagnostics text together (frame changes,
		// the 1s tick, selection changes). One call so notifications can't drift out of sync.
		private void RaiseRadarCard()
		{
			OnPropertyChanged(nameof(RadarDebugText));
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

		/// <summary>Toggles loop playback (no-op until the loop is fully loaded).</summary>
		public void ToggleRadarPlay()
		{
			if (!_isLoopReady)
			{
				return;
			}

			IsPlaying = !_isPlaying;
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

		// 0 = reflectivity, 1 = velocity. Bound to the Radar Loop tool window's Product combo.
		private int _radarProductIndex;

		/// <summary>Selected radar product index (0 = Reflectivity, 1 = Velocity).</summary>
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
					_ = _mapService.SetRadarProductAsync(value == 1 ? "velocity" : "reflectivity");
				}
			}
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
			if (product is not null)
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
			var product = _selectedOption?.Product;
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

		// Appends one radar diagnostic event to the shared debug log (see RadarDebugLog).
		private static void Diag(string message) => Services.RadarDebugLog.Log("vm  " + message);

		// Loads a fresh loop for the site (or clears for "None"): recenters immediately, shows
		// the newest frame first, then backfills older frames; also starts the playback and
		// auto-refresh loops tied to this selection. Cancels the previous selection's work.
		private async Task StartRadarLoopAsync(RadarSite? site)
		{
			Diag($"select site={(site?.Id ?? "none")}");
			_loopCts?.Cancel();
			_loopCts = null;
			IsPlaying = false;
			IsLoopReady = false;

			// Highlight the selected site marker (null clears it).
			if (_isMapReady)
			{
				await _mapService.SetSelectedRadarSiteAsync(site?.Id);
			}

			if (site is null)
			{
				_frameCount = 0;
				_archiveCount = 0;
				_hasLiveFrame = false;
				_liveFrame = null;
				_liveModeText = null;
				_frameTimes = Array.Empty<DateTimeOffset?>();
				_loadedNewestKey = null;
				_lastLivePollAt = null;
				_lastLivePollResult = null;
				_nextLivePollAt = null;
				_lastLiveError = null;
				_loopClickAt = null;
				_firstFrameElapsed = null;
				_allFramesElapsed = null;
				OnPropertyChanged(nameof(MaxFrameIndex));
				OnPropertyChanged(nameof(CurrentFrameTimeText));
				RaiseRadarCard();
				if (_isMapReady)
				{
					await _mapService.ClearRadarAsync();
				}
				return;
			}

			// Start the load timer for this click (frozen once the initial load fully renders).
			_loopClickAt = DateTimeOffset.UtcNow;
			_firstFrameElapsed = null;
			_allFramesElapsed = null;
			_initialLoadDone = false;
			_loadInProgress = true;
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
				keys = await _radarService.GetRecentKeysAsync(site, LoopFrameCount, ct);
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
				Diag($"loadLoop {site.Id}: aborted (keys={keys.Count}, cancelled={ct.IsCancellationRequested})");
				return;
			}

			Diag($"loadLoop {site.Id}: {keys.Count} archive keys, newest={keys[^1]}");

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
			IsLoopReady = false;
			_currentFrameIndex = _frameCount - 1; // newest archive frame
			OnPropertyChanged(nameof(MaxFrameIndex));
			OnPropertyChanged(nameof(CurrentFrameIndex));
			OnPropertyChanged(nameof(CurrentFrameTimeText));

			if (_isMapReady)
			{
				await _mapService.BeginRadarLoopAsync(site);
			}

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
					Diag($"live skip (not newer: {live.VolumeTime:HH:mm:ss}Z <= cur {_liveFrame.VolumeTime:HH:mm:ss}Z)");
					return;
				}

				_liveFrame = live;
				if (_archiveCount < _frameTimes.Length)
				{
					_frameTimes[_archiveCount] = live.VolumeTime;
				}
				Diag($"live UPDATE idx={_archiveCount} {live.VolumeTime:HH:mm:ss}Z");
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
				Diag($"live skip (not newer than archive {an:HH:mm:ss}Z; live {live.VolumeTime:HH:mm:ss}Z)");
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
			Diag($"live APPEND idx={_archiveCount} {live.VolumeTime:HH:mm:ss}Z (frames now {_frameCount})");
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
			Diag($"live poll -> {_lastLivePollResult}");
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
				Diag($"arch[{index}] FAILED: {ex.Message}");
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
			_readyCount++;
			Diag($"ready idx={index} has={hasData} ({_readyCount}/{_frameCount})");

			// First-frame timing: the moment the first frame of this click is decoded + shown.
			if (!_initialLoadDone && _firstFrameElapsed is null && _loopClickAt is { } click)
			{
				_firstFrameElapsed = DateTimeOffset.UtcNow - click;
				Diag($"TIMING first frame in {_firstFrameElapsed.Value.TotalSeconds:0.0}s");
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
				Diag($"TIMING all {_frameCount} frames in {_allFramesElapsed.Value.TotalSeconds:0.0}s");
				RaiseRadarCard();
			}
			_initialLoadDone = true;
		}

		// Advances the loop while playing + ready (~0.5s/frame, with a brief dwell on newest).
		private async Task RunPlaybackAsync(CancellationToken ct)
		{
			try
			{
				using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
				var dwell = 0;
				while (await timer.WaitForNextTickAsync(ct))
				{
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
						keys = await _radarService.GetRecentKeysAsync(site, LoopFrameCount, ct);
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
						Diag($"archive refresh: new vol {keys[^1]} (was {_loadedNewestKey}) -> reload");
						await LoadLoopAsync(site, ct); // a new volume arrived -> rebuild
					}
				}
			}
			catch (OperationCanceledException)
			{
				// Selection changed or app shutting down.
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
					var seconds = _hasLiveFrame ? LiveFrameRefreshSeconds : LiveFrameRetrySeconds;
					_nextLivePollAt = DateTimeOffset.Now.AddSeconds(seconds);
					await Task.Delay(TimeSpan.FromSeconds(seconds), ct);

					if (!ReferenceEquals(_selectedRadarOption?.Site, site))
					{
						return;
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

			// Show the outlook selected at construction (Day 1 Categorical by default)
			// and sync the fill opacity to the slider's initial value.
			var startupProduct = _selectedOption?.Product;
			if (startupProduct is not null)
			{
				await _mapService.ShowOutlookAsync(startupProduct);
			}
			await _mapService.SetOutlookOpacityAsync(_outlookOpacity);
			UpdateOutlookTimes();

			// Provide the radar sites as clickable on-map markers.
			var sites = _radarSiteProvider.GetSites()
				.Select(s => new { id = s.Id, name = s.Name, lng = s.Longitude, lat = s.Latitude });
			await _mapService.ShowRadarSitesAsync(System.Text.Json.JsonSerializer.Serialize(sites));

			// Flag sites with no recent data ("offline") and keep that refreshed.
			_ = RunSiteStatusLoopAsync();
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
					if (_isMapReady)
					{
						await _mapService.SetRadarSitesStatusAsync(System.Text.Json.JsonSerializer.Serialize(offline));
					}
					Diag($"site status: {offline.Count} offline" +
						(offline.Count is > 0 and <= 20 ? $" [{string.Join(",", offline)}]" : ""));
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
