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
		private readonly IRadarSiteProvider _radarSiteProvider;
		private readonly ILevel2RadarService _radarService;

		// Readiness guard: the map page must have reported 'mapReady' before style /
		// outlook commands can succeed. The view calls OnMapsReadyAsync() once the map
		// loads; until then a style change is stored and the overlay is deferred.
		private bool _isMapReady;

		private MapStyle? _selectedStyle;

		// Whether the bottom control ribbon is hidden.
		private bool _isRibbonCollapsed;

		// The region the main map is framed on (CONUS).
		private MapRegion? _mainRegion;

		// Selected SPC outlook day + product option. The option list cascades to
		// whatever's valid for the selected day (plus a leading "None" entry); selecting
		// an option shows that product on the map, or clears it for None.
		private int _selectedDay;
		private IReadOnlyList<OutlookOption> _productOptions = new List<OutlookOption>();
		private OutlookOption? _selectedOption;

		// Fill opacity (0-1) for the outlook polygons; the outlines stay opaque so the
		// basemap reads through. Driven by the ribbon's opacity slider.
		private double _outlookOpacity = 0.05;

		// Suppresses outlook map updates while a day-change cascade re-selects the
		// option (the product combobox transiently nulls its selection mid-swap).
		private bool _suppressOutlookUpdate;

		// Selected radar site option ("None" clears the layer) + radar layer opacity.
		private RadarOption? _selectedRadarOption;
		private double _radarOpacity = 0.85;

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
			Days = _spcOutlookService.AvailableDays;
			_selectedDay = Days.FirstOrDefault();
			RebuildProductOptions();
			_selectedOption = DefaultOptionForDay();

			// Radar site selector: a leading "None" entry plus the curated sites. Defaults
			// to None so launch is unchanged; selecting a site loads its loop.
			var radarOptions = new List<RadarOption> { new("None", null) };
			radarOptions.AddRange(_radarSiteProvider.GetSites().Select(s => new RadarOption($"{s.Id} — {s.Name}", s)));
			RadarOptions = radarOptions;
			_selectedRadarOption = RadarOptions[0];
		}

		public IReadOnlyList<MapStyle> AvailableStyles { get; }

		/// <summary>The region the main, full-window map is framed on.</summary>
		public MapRegion? MainRegion => _mainRegion;

		/// <summary>
		/// Whether the bottom control ribbon is collapsed (hidden). Pure view state.
		/// Hand-written INPC.
		/// </summary>
		public bool IsRibbonCollapsed
		{
			get => _isRibbonCollapsed;
			set
			{
				if (_isRibbonCollapsed == value)
				{
					return;
				}

				_isRibbonCollapsed = value;
				OnPropertyChanged();
				OnPropertyChanged(nameof(IsRibbonExpanded));
			}
		}

		/// <summary>Convenience inverse of <see cref="IsRibbonCollapsed"/>.</summary>
		public bool IsRibbonExpanded => !_isRibbonCollapsed;

		/// <summary>Outlook days that have products (1-8); static for the app lifetime.</summary>
		public IReadOnlyList<int> Days { get; }

		/// <summary>
		/// The selected outlook day. Changing it cascades the product list and auto-
		/// selects a product for the new day so an overlay stays visible.
		/// </summary>
		public int SelectedDay
		{
			get => _selectedDay;
			set
			{
				if (_selectedDay == value)
				{
					return;
				}

				_selectedDay = value;
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
		}

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

		// Loads a fresh loop for the site (or clears for "None"): recenters immediately, shows
		// the newest frame first, then backfills older frames; also starts the playback and
		// auto-refresh loops tied to this selection. Cancels the previous selection's work.
		private async Task StartRadarLoopAsync(RadarSite? site)
		{
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
				_frameTimes = Array.Empty<DateTimeOffset?>();
				_loadedNewestKey = null;
				OnPropertyChanged(nameof(MaxFrameIndex));
				OnPropertyChanged(nameof(CurrentFrameTimeText));
				if (_isMapReady)
				{
					await _mapService.ClearRadarAsync();
				}
				return;
			}

			// Note: no flyTo — load the radar at the user's current view; they pan/zoom freely.
			var cts = new CancellationTokenSource();
			_loopCts = cts;

			await LoadLoopAsync(site, cts.Token);

			_ = RunPlaybackAsync(cts.Token);
			_ = RunRefreshAsync(site, cts.Token);
		}

		// Lists the recent volumes, begins a loop, shows the newest, then backfills the rest.
		private async Task LoadLoopAsync(RadarSite site, CancellationToken ct)
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
				return;
			}

			_frameCount = keys.Count;
			_frameTimes = new DateTimeOffset?[_frameCount];
			_readyCount = 0;
			_loadedNewestKey = keys[_frameCount - 1];
			IsLoopReady = false;
			_currentFrameIndex = _frameCount - 1; // newest
			OnPropertyChanged(nameof(MaxFrameIndex));
			OnPropertyChanged(nameof(CurrentFrameIndex));
			OnPropertyChanged(nameof(CurrentFrameTimeText));

			if (_isMapReady)
			{
				await _mapService.BeginRadarLoopAsync(site);
			}

			// Newest first (immediate display), then the older frames.
			await EnsureAndAddFrameAsync(site, keys, _frameCount - 1, ct);
			for (var i = 0; i < _frameCount - 1 && !ct.IsCancellationRequested; i++)
			{
				await EnsureAndAddFrameAsync(site, keys, i, ct);
			}
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
			catch
			{
				// Skip a bad frame; the rest of the loop still loads.
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
			OnPropertyChanged(nameof(CurrentFrameTimeText));
			if (_readyCount >= _frameCount && _frameCount > 0)
			{
				IsLoopReady = true;
				OnPropertyChanged(nameof(CurrentFrameTimeText));
			}
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
						await LoadLoopAsync(site, ct); // a new volume arrived -> rebuild
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

			// Provide the radar sites as clickable on-map markers.
			var sites = _radarSiteProvider.GetSites()
				.Select(s => new { id = s.Id, name = s.Name, lng = s.Longitude, lat = s.Latitude });
			await _mapService.ShowRadarSitesAsync(System.Text.Json.JsonSerializer.Serialize(sites));
		}

		public event PropertyChangedEventHandler? PropertyChanged;

		private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
		{
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
		}
	}
}
