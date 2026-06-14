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

		// Level II radar data layer (fetch + cache of .V06 volumes). Kept here because
		// MainWindow owns the WebView2 host mapping for its cache folder.
		private readonly ILevel2RadarService _radarService;

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
			_radarService = new Level2RadarService();
			var radarSiteProvider = new RadarSiteProvider();

			ViewModel = new MapViewModel(mapService, styleProvider, regionProvider, _spcOutlookService, radarSiteProvider, _radarService);

			ExtendsContentIntoTitleBar = true;
			InitializeComponent();

			// Start maximized.
			(AppWindow.Presenter as OverlappedPresenter)?.Maximize();

			_ = InitializeMapAsync();

			// Attempt a network refresh in the background: the app stays usable offline
			// from the existing cache while fresh outlooks download. A scheduled/periodic
			// refresh would later be driven from here (or a hosted timer) calling the
			// same RefreshAllAsync.
			_ = RefreshOutlooksInBackgroundAsync();
		}

		private async Task RefreshOutlooksInBackgroundAsync()
		{
			try
			{
				var results = await _spcOutlookService.RefreshAllAsync();
				var failed = results.Count(r => r.Status is SpcOutlookFetchStatus.FailedCacheKept
					or SpcOutlookFetchStatus.FailedNoCache);
				System.Diagnostics.Debug.WriteLine($"[SPC] refreshed {results.Count} products, {failed} failed.");
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"[SPC] refresh aborted: {ex.Message}");
			}
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
				"?key=main&interactive=true" +
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

			// Serve the large tile data file from its fixed external location.
			webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
				hostName: "mapdata",
				folderPath: @"C:\Users\jhamm\OneDrive\Desktop",
				accessKind: CoreWebView2HostResourceAccessKind.Allow);

			// Serve the cached SPC outlook GeoJSON (written by SpcOutlookService) so the
			// page can load each product from https://spcoutlooks/<id>.geojson. The
			// service owns the folder; MainWindow owns the WebView2 mapping.
			webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
				hostName: SpcOutlookService.CacheHostName,
				folderPath: _spcOutlookService.CacheDirectory,
				accessKind: CoreWebView2HostResourceAccessKind.Allow);

			// Serve the cached Level II volumes (written by Level2RadarService) so the page
			// can load them from https://radarlevel2/<site>.V06 and decode them in JS.
			webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
				hostName: Level2RadarService.CacheHostName,
				folderPath: _radarService.CacheDirectory,
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

				// Diagnostics from radar.js surface in the VS Output window.
				if (type == "radarLog")
				{
					if (root.TryGetProperty("msg", out var msgEl))
					{
						System.Diagnostics.Debug.WriteLine($"[radar-js] {msgEl.GetString()}");
					}
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

		private void OnCollapseRibbonClick(object sender, RoutedEventArgs e)
		{
			ViewModel.IsRibbonCollapsed = true;
		}

		private void OnRevealRibbonClick(object sender, RoutedEventArgs e)
		{
			ViewModel.IsRibbonCollapsed = false;
		}

		private void OnTogglePlayClick(object sender, RoutedEventArgs e)
		{
			ViewModel.ToggleRadarPlay();
		}

		// x:Bind function mapping a bool to Visibility for the ribbon / reveal handle.
		// Used instead of a value converter because x:Bind's converter lookup isn't
		// available with a Window as the binding root.
		public Visibility VisibleWhen(bool value) =>
			value ? Visibility.Visible : Visibility.Collapsed;

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
