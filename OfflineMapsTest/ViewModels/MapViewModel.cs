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
		private readonly ILocationService _locationService;

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

		// Outlook refresh schedule (set by MainWindow each ~15-min cycle) for the Outlook tool
		// window's next-update progress bar.
		private DateTimeOffset? _outlookCycleStart;
		private DateTimeOffset? _nextOutlookRefreshAt;

		public MapViewModel(IMapService mapService, IStyleProvider styleProvider, IRegionProvider regionProvider, ISpcOutlookService spcOutlookService, ISpcWatchService watchService, IRadarSiteProvider radarSiteProvider, ILevel2RadarService radarService, ILocationService locationService, IDowEventProvider dowEventProvider)
		{
			_mapService = mapService;
			_styleProvider = styleProvider;
			_regionProvider = regionProvider;
			_spcOutlookService = spcOutlookService;
			_watchService = watchService;
			_locationService = locationService;

			// The radar subsystem (sites, loop, live frame, past-event, DOW, card, legend, inspector)
			// lives in its own view model; the transport-bar section controls bind slices of it.
			Radar = new RadarViewModel(mapService, radarSiteProvider, radarService, dowEventProvider);

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
		}

		/// <summary>The radar subsystem view model (sites, loop, live frame, past-event, DOW, card,
		/// color scale, inspector). The transport-bar section controls bind to this.</summary>
		public RadarViewModel Radar { get; }

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

		// NOTE: the transport bar's show/hide state is pure view state and now lives on the
		// TransportBar control itself (Controls/TransportBar), not here.


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



		// Elapsed fraction (0..100) of the current wait — 0 right after an update, ~100 just before
		// the next. Returns 0 when there's nothing scheduled (e.g. between cycles).
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

		// App-lifetime 1s tick that advances the outlook next-update progress bar (independent of any
		// radar loop, so the outlook bar updates even when no loop is active).
		private async Task RunProgressTickAsync()
		{
			using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
			while (await timer.WaitForNextTickAsync())
			{
				OnPropertyChanged(nameof(OutlookNextUpdateProgress));
				OnPropertyChanged(nameof(OutlookNextUpdateText));
			}
		}


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

			// Hand off radar startup (site markers, offline-status loop, radar progress bar).
			await Radar.OnMapsReadyAsync();

			// Drive the SPC outlook next-update progress bar.
			_ = RunProgressTickAsync();
		}

		public event PropertyChangedEventHandler? PropertyChanged;

		private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
		{
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
		}
	}
}
