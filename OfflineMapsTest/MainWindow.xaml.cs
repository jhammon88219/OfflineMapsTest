using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using OfflineMapsTest.Models;
using OfflineMapsTest.Services;
using OfflineMapsTest.ViewModels;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OfflineMapsTest
{
	public sealed partial class MainWindow : Window, IMapView
	{
		public MapViewModel ViewModel { get; }

		// Set once the map page reports 'mapReady'; guards against re-entry on reload.
		private bool _mapReady;

		// SPC outlook data layer (fetch + cache of severe/fire-weather GeoJSON). Kept
		// here because MainWindow owns the WebView2 host mapping for its cache folder.
		private readonly ISpcOutlookService _spcOutlookService;

		// SPC watch-box data layer (fetch + cache of active watch GeoJSON). Same reason —
		// MainWindow owns the WebView2 host mapping for its cache folder.
		private readonly ISpcWatchService _spcWatchService;

		// Level II radar data layer (fetch + cache of .V06 volumes). Kept here because
		// MainWindow owns the WebView2 host mapping for its cache folder.
		private readonly ILevel2RadarService _radarService;

		// App settings (offline basemap folder, …). Read when mapping the "mapdata" WebView host.
		private readonly ISettingsService _settingsService;

		public MainWindow()
		{
			// Build the MVVM chain BEFORE InitializeComponent so x:Bind sees a non-null
			// ViewModel when the bindings first evaluate. Maximize() below realizes the
			// window and can trigger that first evaluation synchronously, so the view
			// model must already be set. (no DI container yet; MainWindow is the IMapView,
			// so the service drives the map through it.)
			var mapService = new MapService(this);
			var styleProvider = new StyleProvider();
			var regionProvider = new RegionProvider();

			// SPC outlook service - wired alongside the others. It owns an on-disk
			// GeoJSON cache and never touches WebView2; we map its cache folder to a
			// virtual host in InitializeWebViewAsync.
			_spcOutlookService = new SpcOutlookService();
			_spcWatchService = new SpcWatchService();
			_radarService = new Level2RadarService();
			var radarSiteProvider = new RadarSiteProvider();
			var locationService = new LocationService();

			// App settings (packaged-app LocalSettings). Holds the offline basemap folder, read by
			// InitializeWebViewAsync below to map the "mapdata" host (default = runtime-resolved Desktop).
			_settingsService = new SettingsService();

			// Start the dedicated radar diagnostics for this run: a per-launch JSONL event stream +
			// a derived markdown report under a package-local Diagnostics/ folder (never auto-deleted;
			// see RadarDiagnostics). This is the primary tool for chasing intermittent radar issues.
			Services.RadarDiagnostics.Init(
				System.IO.Path.Combine(_radarService.CacheDirectory, "Diagnostics"));

			var dowEventProvider = new DowEventProvider();
			ViewModel = new MapViewModel(mapService, styleProvider, regionProvider, _spcOutlookService, _spcWatchService, radarSiteProvider, _radarService, locationService, dowEventProvider);

			ExtendsContentIntoTitleBar = true;
			InitializeComponent();

			// Keep the dock's site list scrolled to the selected site when a map-marker pick drives
			// the selection (the SelectedItem binding highlights the row but doesn't auto-scroll).
			ViewModel.PropertyChanged += OnViewModelPropertyChanged;

			// Start maximized.
			(AppWindow.Presenter as OverlappedPresenter)?.Maximize();

			_ = InitializeMapAsync();

			// Attempt a network refresh in the background: the app stays usable offline from the
			// existing cache while fresh outlooks download, then keeps refreshing on a timer so a
			// long-running session doesn't sit on stale outlooks.
			_ = RefreshOutlooksInBackgroundAsync();

			// SPC watch boxes refresh on their own cadence (and expire over time).
			_ = RefreshWatchesInBackgroundAsync();

			// Write a final flush + report on close so the run's last events aren't lost between
			// the ~2 s background flushes.
			Closed += (_, _) => Services.RadarDiagnostics.FlushAll();
		}

		// SPC products update a handful of times a day at scheduled issuances; poll periodically so
		// we catch new ones. Conditional GETs make each cycle cheap (mostly 304s when unchanged).
		private static readonly TimeSpan OutlookRefreshInterval = TimeSpan.FromMinutes(15);

		private async Task RefreshOutlooksInBackgroundAsync()
		{
			// Refresh immediately on launch, then every OutlookRefreshInterval for the app's life.
			var first = true;
			using var timer = new PeriodicTimer(OutlookRefreshInterval);
			do
			{
				try
				{
					var results = await _spcOutlookService.RefreshAllAsync();
					var updated = results.Count(r => r.Status is SpcOutlookFetchStatus.Updated);
					var failed = results.Count(r => r.Status is SpcOutlookFetchStatus.FailedCacheKept
						or SpcOutlookFetchStatus.FailedNoCache);
					System.Diagnostics.Debug.WriteLine($"[SPC] refreshed {results.Count} products, {updated} updated, {failed} failed.");

					// Re-apply the current outlook on the UI thread on launch (so a first-run empty
					// cache overlay appears and the issued/valid readout picks up times) and whenever
					// a later cycle actually pulled new data — but not when a periodic cycle was all
					// 304s, so we don't needlessly re-render the overlay every 15 minutes.
					if (first || updated > 0)
					{
						DispatcherQueue.TryEnqueue(() => ViewModel.OnOutlooksRefreshed());
					}
				}
				catch (Exception ex)
				{
					System.Diagnostics.Debug.WriteLine($"[SPC] refresh aborted: {ex.Message}");
				}
				first = false;

				// Tell the VM when the next periodic refresh is roughly due, so the Outlook tool
				// window can show a countdown/progress to it (the PeriodicTimer fires on a fixed
				// cadence; the refresh itself takes seconds, negligible vs the 15-min interval).
				var cycleStart = DateTimeOffset.Now;
				DispatcherQueue.TryEnqueue(() =>
					ViewModel.SetOutlookRefreshSchedule(cycleStart, cycleStart + OutlookRefreshInterval));
			}
			while (await timer.WaitForNextTickAsync());
		}

		// SPC watches change on roughly hourly scales but expire continuously; a few-minute refresh
		// keeps the active set current (the service re-filters to in-effect watches each cycle).
		private static readonly TimeSpan WatchRefreshInterval = TimeSpan.FromMinutes(2);

		private async Task RefreshWatchesInBackgroundAsync()
		{
			var first = true;
			using var timer = new PeriodicTimer(WatchRefreshInterval);
			do
			{
				try
				{
					var result = await _spcWatchService.RefreshAsync();
					System.Diagnostics.Debug.WriteLine($"[SPC] watches refresh: {result.Status} active={result.ActiveCount} {result.Message}");

					// Re-point the page at the cache so it reloads — on launch (first-run empty cache)
					// and whenever a cycle pulled fresh data.
					if (first || result.Status is SpcWatchFetchStatus.Updated)
					{
						DispatcherQueue.TryEnqueue(() => ViewModel.OnWatchesRefreshed());
					}
				}
				catch (Exception ex)
				{
					System.Diagnostics.Debug.WriteLine($"[SPC] watches refresh aborted: {ex.Message}");
				}
				first = false;
			}
			while (await timer.WaitForNextTickAsync());
		}

		private async Task InitializeMapAsync()
		{
			// The page loads the currently-selected basemap directly (no flash to the
			// default, then re-style). The view model's default is Data Viz Black.
			var styleFile = ViewModel.SelectedStyle?.FileName ?? "style.json";
			var main = ViewModel.MainRegion;
			await InitializeWebViewAsync(MainMapWebView, BuildMapUrl(main, main?.Zoom ?? 4, styleFile));
		}

		// Builds the page URL for the map: framed at the given center/zoom, on the given
		// basemap. The page posts 'mapReady' once its MapLibre map's 'load' fires.
		private static string BuildMapUrl(MapRegion? region, double zoom, string styleFile)
		{
			var lng = region?.Longitude ?? -95.5;
			var lat = region?.Latitude ?? 37.0;
			return "https://mapassets/map.html" +
				"?interactive=true" +
				$"&style={styleFile}" +
				$"&lng={lng.ToString(CultureInfo.InvariantCulture)}" +
				$"&lat={lat.ToString(CultureInfo.InvariantCulture)}" +
				$"&zoom={zoom.ToString(CultureInfo.InvariantCulture)}";
		}

		private async Task InitializeWebViewAsync(WebView2 webView, string url)
		{
			// Dark default so there's no white flash before the page (and MapLibre) paint.
			webView.DefaultBackgroundColor = Microsoft.UI.Colors.Black;
			await webView.EnsureCoreWebView2Async();

			// Serve the bundled map assets (style, glyphs, sprites, libraries) offline.
			webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
				hostName: "mapassets",
				folderPath: Path.Combine(AppContext.BaseDirectory, "Assets", "Map"),
				accessKind: CoreWebView2HostResourceAccessKind.Allow);

			// Serve the large (~29 GB) basemap PMTiles file from the user-configured folder (default =
			// runtime-resolved Desktop; never a hardcoded path). The file is far too big to bundle, so
			// it stays external; the Settings dialog lets the user point at it. Mapping the chosen folder
			// (not a whole user folder) also narrows what the WebView can read.
			webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
				hostName: "mapdata",
				folderPath: _settingsService.MapDataFolder,
				accessKind: CoreWebView2HostResourceAccessKind.Allow);

			// Serve the cached SPC outlook GeoJSON (written by SpcOutlookService) so the
			// page can load each product from https://spcoutlooks/<id>.geojson. The
			// service owns the folder; MainWindow owns the WebView2 mapping.
			webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
				hostName: SpcOutlookService.CacheHostName,
				folderPath: _spcOutlookService.CacheDirectory,
				accessKind: CoreWebView2HostResourceAccessKind.Allow);

			// Serve the cached SPC watch GeoJSON (written by SpcWatchService) so the page can load
			// https://spcwatches/watches.geojson and draw the watch boxes.
			webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
				hostName: SpcWatchService.CacheHostName,
				folderPath: _spcWatchService.CacheDirectory,
				accessKind: CoreWebView2HostResourceAccessKind.Allow);

			// Serve the cached Level II volumes (written by Level2RadarService) so the page
			// can load them from https://radarlevel2/<site>.V06 and decode them in JS.
			webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
				hostName: Level2RadarService.CacheHostName,
				folderPath: _radarService.CacheDirectory,
				accessKind: CoreWebView2HostResourceAccessKind.Allow);

			// Serve the bundled curated DOW (mobile-radar) frames (Assets/DowEvents/*.dow.json) so the
			// page can fetch https://dowevents/<file>.dow.json and render it. The folder ships with the
			// app (its README is Content), so it normally exists; guard the create for the rare case it
			// doesn't (a read-only package dir would throw otherwise).
			try { Directory.CreateDirectory(DowEventProvider.EventsDirectory); } catch { /* folder ships with the app */ }
			webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
				hostName: DowEventProvider.HostName,
				folderPath: DowEventProvider.EventsDirectory,
				accessKind: CoreWebView2HostResourceAccessKind.Allow);

			webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
			webView.Source = new Uri(url);
		}

		private async void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
		{
			var message = args.TryGetWebMessageAsString();
			if (string.IsNullOrEmpty(message))
			{
				return;
			}

			try
			{
				using var doc = JsonDocument.Parse(message);
				var root = doc.RootElement;
				if (root.ValueKind != JsonValueKind.Object ||
					!root.TryGetProperty("type", out var typeEl))
				{
					return;
				}
				var type = typeEl.GetString();

				// Free-form diagnostics line from radar.js (also echoed to the VS Output window).
				if (type == "radarLog")
				{
					if (root.TryGetProperty("msg", out var msgEl))
					{
						var msg = msgEl.GetString();
						System.Diagnostics.Debug.WriteLine($"[radar-js] {msg}");
						if (msg is not null) Services.RadarDiagnostics.JsLog(msg);
					}
					return;
				}

				// Structured per-frame decode metrics from radar.js — recorded with suspect-frame
				// evaluation + .V06 quarantine in the diagnostics service.
				if (type == "radarFrame")
				{
					Services.RadarDiagnostics.JsFrame(root);
					return;
				}

				// Render-health signal from radar.js (blank / error / recovered / context lost).
				if (type == "radarRender")
				{
					Services.RadarDiagnostics.JsRender(root);
					return;
				}

				// The active product's color ramp (pushed from radar-ramps.js) — feeds the legend.
				if (type == "radarRamp")
				{
					if (root.TryGetProperty("ramp", out var rampEl))
					{
						var ramp = JsonSerializer.Deserialize<Models.RadarRampInfo>(
							rampEl.GetRawText(),
							new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
						ViewModel.SetColorScale(ramp);
					}
					return;
				}

				// Inspect-mode value under the cursor (pushed from radar.js as the pointer moves) —
				// drives the live marker on the color-scale bar. has=false clears it.
				if (type == "radarInspect")
				{
					var has = root.TryGetProperty("has", out var hasEl) && hasEl.GetBoolean();
					double? val = has && root.TryGetProperty("value", out var valEl) ? valEl.GetDouble() : null;
					ViewModel.SetInspectValue(val);
					return;
				}

				// A radar site marker was clicked.
				if (type == "radarSiteClick")
				{
					if (root.TryGetProperty("id", out var siteEl))
					{
						ViewModel.OnRadarSiteClicked(siteEl.GetString());
					}
					return;
				}

				// A map marker (e.g. the user-location marker) was clicked — select it for editing.
				if (type == "markerClick")
				{
					if (root.TryGetProperty("id", out var mIdEl))
					{
						ViewModel.OnMarkerClicked(mIdEl.GetString());
					}
					return;
				}

				// A draggable marker was moved — record the refined position.
				if (type == "markerMoved")
				{
					if (root.TryGetProperty("id", out var mvIdEl) &&
						root.TryGetProperty("lng", out var lngEl) &&
						root.TryGetProperty("lat", out var latEl))
					{
						ViewModel.OnMarkerMoved(mvIdEl.GetString(), lngEl.GetDouble(), latEl.GetDouble());
					}
					return;
				}

				// A radar loop frame finished decoding in the WebView.
				if (type == "radarFrameReady")
				{
					if (root.TryGetProperty("index", out var idxEl))
					{
						var hasData = root.TryGetProperty("hasData", out var hd) && hd.GetBoolean();
						ViewModel.OnRadarFrameReady(idxEl.GetInt32(), hasData);
					}
					return;
				}

				// The page posts {type:"mapReady"} once its map's 'load' fires. Enable
				// style / outlook commands the first time we see it.
				if (type == "mapReady" && !_mapReady)
				{
					_mapReady = true;
					await ViewModel.OnMapsReadyAsync();
				}
			}
			catch (JsonException)
			{
				// Ignore malformed messages from the page.
			}
		}

		private void OnTogglePlayClick(object sender, RoutedEventArgs e)
		{
			ViewModel.ToggleRadarPlay();
		}

		private void OnStopLoopClick(object sender, RoutedEventArgs e)
		{
			ViewModel.StopRadarLoop();
		}

		private void OnLoadPastEventClick(object sender, RoutedEventArgs e)
		{
			_ = ViewModel.LoadSelectedPastEventAsync();
		}

		private void OnLoadDowEventClick(object sender, RoutedEventArgs e)
		{
			_ = ViewModel.LoadDowEventAsync();
		}

		private void OnClearDowEventClick(object sender, RoutedEventArgs e)
		{
			_ = ViewModel.ClearDowEventAsync();
		}

		private void OnToggleRadarSitesClick(object sender, RoutedEventArgs e)
		{
			ViewModel.ToggleRadarSitesVisible();
		}

		// Radar Loop tool window: hard-reset the current loop (dump + reload from scratch).
		private void OnResetLoopClick(object sender, RoutedEventArgs e)
		{
			ViewModel.ResetRadarLoop();
		}

		// Left tool-window dock: collapse + reveal both just flip the dock state.
		private void OnToggleDockClick(object sender, RoutedEventArgs e)
		{
			ViewModel.ToggleDock();
		}

		// Tools header gear: open the Settings dialog. Changing the basemap folder is persisted and
		// applied on the next launch (the map host is mapped once at startup; re-mapping live would
		// need a page reload that re-runs the one-time startup loops, so a restart is the clean path).
		private async void OnOpenSettingsClick(object sender, RoutedEventArgs e)
		{
			var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
			var dialog = new SettingsDialog(_settingsService, hwnd) { XamlRoot = Content.XamlRoot };
			var before = _settingsService.MapDataFolder;

			var result = await dialog.ShowAsync();
			if (result != ContentDialogResult.Primary)
			{
				return;
			}

			var chosen = dialog.SelectedFolder;
			if (string.IsNullOrWhiteSpace(chosen) || string.Equals(chosen, before, StringComparison.OrdinalIgnoreCase))
			{
				return;
			}

			_settingsService.MapDataFolder = chosen;
			await new ContentDialog
			{
				Title = "Restart to apply",
				Content = "The map data folder was updated. Restart the app to load the basemap from the new location.",
				CloseButtonText = "OK",
				XamlRoot = Content.XamlRoot,
			}.ShowAsync();
		}

		// Radar Loop tool window: toggle inspect mode (read the value under the cursor). IsChecked is
		// bound one-way (bool? target, bool source), so the click drives the VM from the button state.
		private void OnToggleInspectClick(object sender, RoutedEventArgs e)
		{
			if (sender is Microsoft.UI.Xaml.Controls.Primitives.ToggleButton tb)
			{
				ViewModel.IsInspecting = tb.IsChecked == true;
			}
		}

		// Map tool window: resolve + show the user's location (OS, then IP fallback).
		private async void OnMyLocationClick(object sender, RoutedEventArgs e)
		{
			await ViewModel.ShowMyLocationAsync();
		}

		// Selected Marker tool window: remove the selected marker from the map.
		private void OnRemoveMarkerClick(object sender, RoutedEventArgs e)
		{
			ViewModel.RemoveSelectedMarker();
		}

		// The dock's "Radar Sites" list is two-way bound to ViewModel.SelectedSiteRow, so a row
		// click selects the site and a map-marker pick highlights the row. ScrollIntoView isn't
		// automatic on a programmatic SelectedItem change, so do it here when the selection moves.
		private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
		{
			if (e.PropertyName == nameof(MapViewModel.SelectedSiteRow) && ViewModel.SelectedSiteRow is { } row)
			{
				RadarSitesListView?.ScrollIntoView(row);
			}
		}

		// VS-style "active tool window" highlight: the card whose contents hold focus gets an
		// accent border; the previously-active card reverts to the default stroke. GotFocus is a
		// routed event, so it bubbles up from whichever control inside the card was focused. We
		// don't clear on focus leaving to the map — the last-focused card stays highlighted, as VS does.
		private Microsoft.UI.Xaml.Controls.Border? _activeToolCard;
		private Microsoft.UI.Xaml.Media.Brush? _accentCardBrush;
		private Microsoft.UI.Xaml.Media.Brush? _defaultCardBrush;

		private void OnCardGotFocus(object sender, RoutedEventArgs e)
		{
			if (sender is not Microsoft.UI.Xaml.Controls.Border card || ReferenceEquals(card, _activeToolCard))
			{
				return;
			}

			// Capture the themed default stroke once (before we override any), and build the accent
			// brush from the system accent color (with a safe fallback so a missing resource can't crash).
			_defaultCardBrush ??= card.BorderBrush;
			_accentCardBrush ??= new Microsoft.UI.Xaml.Media.SolidColorBrush(SystemAccentColor());

			if (_activeToolCard is not null)
			{
				_activeToolCard.BorderBrush = _defaultCardBrush;
			}
			card.BorderBrush = _accentCardBrush;
			_activeToolCard = card;
		}

		private static Windows.UI.Color SystemAccentColor()
		{
			try
			{
				if (Application.Current.Resources.ContainsKey("SystemAccentColor") &&
					Application.Current.Resources["SystemAccentColor"] is Windows.UI.Color c)
				{
					return c;
				}
			}
			catch { /* fall through */ }
			return Windows.UI.Color.FromArgb(0xFF, 0x00, 0x78, 0xD4); // WinUI default accent
		}

		// x:Bind function mapping a bool to Visibility for the ribbon / reveal handle.
		// Used instead of a value converter because x:Bind's converter lookup isn't
		// available with a Window as the binding root.
		public Visibility VisibleWhen(bool value) =>
			value ? Visibility.Visible : Visibility.Collapsed;

		// x:Bind function: logical AND of two bools (re-evaluates when either changes). Used to gate
		// "Reset loop" on both having a loop AND being in live mode.
		public bool AllTrue(bool a, bool b) => a && b;

		// x:Bind function: the radar card's status-dot color for a freshness level (same reason
		// as VisibleWhen — no converter lookup with a Window root).
		public Microsoft.UI.Xaml.Media.Brush RadarDotBrush(RadarFreshness status) =>
			new Microsoft.UI.Xaml.Media.SolidColorBrush(status switch
			{
				RadarFreshness.Live => Windows.UI.Color.FromArgb(0xFF, 0x3F, 0xB9, 0x50),   // green
				RadarFreshness.Recent => Windows.UI.Color.FromArgb(0xFF, 0xE3, 0xB3, 0x41), // amber
				RadarFreshness.Stale => Windows.UI.Color.FromArgb(0xFF, 0xF8, 0x5E, 0x5E),  // red
				_ => Windows.UI.Color.FromArgb(0xFF, 0x6E, 0x7A, 0x86)                       // gray
			});

		// x:Bind function: the Segoe Fluent glyph indicating which method produced the location —
		// a map pin for the (precise) OS fix, a globe for the (approximate) IP fallback, an info
		// glyph otherwise (locating / unavailable).
		public string LocationGlyph(LocationSource source) => source switch
		{
			LocationSource.Manual => "",     // Edit (manually adjusted)
			LocationSource.OperatingSystem => "", // MapPin
			LocationSource.IpAddress => "",       // Globe
			_ => ""                                // Info
		};

		// x:Bind function: color for the location source affordance (green = precise OS fix,
		// amber = approximate IP, gray = none). Same no-converter pattern as RadarDotBrush.
		public Microsoft.UI.Xaml.Media.Brush LocationBrush(LocationSource source) =>
			new Microsoft.UI.Xaml.Media.SolidColorBrush(source switch
			{
				LocationSource.Manual => Windows.UI.Color.FromArgb(0xFF, 0x2F, 0x8F, 0xFF),          // blue (manually adjusted)
				LocationSource.OperatingSystem => Windows.UI.Color.FromArgb(0xFF, 0x3F, 0xB9, 0x50), // green
				LocationSource.IpAddress => Windows.UI.Color.FromArgb(0xFF, 0xE3, 0xB3, 0x41),       // amber
				_ => Windows.UI.Color.FromArgb(0xFF, 0x6E, 0x7A, 0x86)                                // gray
			});

		// x:Bind function: builds the color-scale legend bar as a LinearGradientBrush straight from the
		// ramp's stops (the same data that colors the gates — honest, not a hand-drawn copy). Smooth
		// gradient for interpolated ramps (velocity/CC); hard NWS bands for discrete ramps (reflectivity)
		// via duplicated stops at each band boundary.
		public Microsoft.UI.Xaml.Media.Brush RampBrush(Models.RadarRampInfo? ramp)
		{
			var brush = new Microsoft.UI.Xaml.Media.LinearGradientBrush
			{
				StartPoint = new Windows.Foundation.Point(0, 0.5),
				EndPoint = new Windows.Foundation.Point(1, 0.5),
			};
			if (ramp?.Stops is not { Count: > 0 } stops)
			{
				return brush;
			}

			double min = ramp.Min, max = ramp.Max, span = max - min;
			if (span <= 0) span = 1;
			double Off(double v) => Math.Clamp((v - min) / span, 0, 1);

			static Windows.UI.Color ColorOf(Models.RadarRampStop s)
			{
				var c = s.Color;
				byte r = (byte)(c.Length > 0 ? c[0] : 0), g = (byte)(c.Length > 1 ? c[1] : 0), b = (byte)(c.Length > 2 ? c[2] : 0);
				return Windows.UI.Color.FromArgb(0xFF, r, g, b);
			}

			if (ramp.Interpolate)
			{
				foreach (var s in stops)
				{
					brush.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop { Offset = Off(s.V), Color = ColorOf(s) });
				}
			}
			else
			{
				// Discrete: a value in [v_i, v_{i+1}) takes color_i, so hold each band's color flat and
				// jump at the boundary (two stops at the same offset = a hard edge).
				brush.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop { Offset = 0, Color = ColorOf(stops[0]) });
				for (int i = 1; i < stops.Count; i++)
				{
					double off = Off(stops[i].V);
					brush.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop { Offset = off, Color = ColorOf(stops[i - 1]) });
					brush.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop { Offset = off, Color = ColorOf(stops[i]) });
				}
			}
			return brush;
		}

		// x:Bind functions positioning the inspect marker over the color-scale bar via a 3-column grid
		// (left-star | marker | right-star): the star weights are the value's fraction along the ramp,
		// so the marker lands at the right spot without needing the bar's pixel width.
		public Microsoft.UI.Xaml.GridLength InspectLeftStar(double fraction) =>
			new Microsoft.UI.Xaml.GridLength(Math.Clamp(fraction, 0, 1), Microsoft.UI.Xaml.GridUnitType.Star);

		public Microsoft.UI.Xaml.GridLength InspectRightStar(double fraction) =>
			new Microsoft.UI.Xaml.GridLength(1 - Math.Clamp(fraction, 0, 1), Microsoft.UI.Xaml.GridUnitType.Star);

		/// <summary>
		/// IMapView seam: the ONLY place that touches WebView2 / ExecuteScriptAsync.
		/// Valid only once the map's CoreWebView2 has initialized.
		/// </summary>
		public async Task<string> RunScriptAsync(string javaScript)
		{
			if (MainMapWebView.CoreWebView2 is null)
			{
				return string.Empty;
			}

			return await MainMapWebView.CoreWebView2.ExecuteScriptAsync(javaScript);
		}
	}
}
