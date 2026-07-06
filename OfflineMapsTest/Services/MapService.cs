using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using OfflineMapsTest.Models;

namespace OfflineMapsTest.Services
{
	/// <summary>
	/// Default <see cref="IMapService"/>. Drives the map exclusively through the
	/// <see cref="IMapView"/> seam, so it never touches WebView2 or the UI.
	/// </summary>
	public sealed class MapService : IMapService
	{
		private readonly IMapView _mapView;

		public MapService(IMapView mapView)
		{
			_mapView = mapView;
		}

		public Task ApplyStyleAsync(MapStyle style) =>
			_mapView.RunScriptAsync(Call("applyStyle", $"https://mapassets/{style.FileName}"));

		// SPC outlooks load the GeoJSON from the product's local cache URL.
		public Task ShowOutlookAsync(SpcOutlookProduct product) =>
			_mapView.RunScriptAsync(Call("showOutlook", product.LocalUrl));

		public Task ClearOutlookAsync() =>
			_mapView.RunScriptAsync(Call("clearOutlook"));

		public Task SetOutlookOpacityAsync(double opacity) =>
			_mapView.RunScriptAsync(Call("setOutlookOpacity", opacity));

		// SPC watch boxes: point the page at the cached watch GeoJSON, and toggle the layers.
		public Task SetWatchSourceAsync(string url) =>
			_mapView.RunScriptAsync(Call("setWatchSource", url));

		public Task SetWatchesVisibleAsync(bool visible) =>
			_mapView.RunScriptAsync(Call("setWatchesVisible", visible));

		// The loop is driven frame-by-frame: begin (with the site's antenna coords, needed to
		// project the gates), then add each cached volume URL as a frame, then show by index.
		public Task BeginRadarLoopAsync(RadarSite site) =>
			_mapView.RunScriptAsync(Call("radarBeginLoop", site.Latitude, site.Longitude));

		public Task AddRadarFrameAsync(string localUrl, int index) =>
			_mapView.RunScriptAsync(Call("radarAddFrame", localUrl, index));

		public Task ShowRadarFrameAsync(int index) =>
			_mapView.RunScriptAsync(Call("radarShowFrame", index));

		// mappingJson is a JSON array of [from,to] index pairs; Call single-quotes it and the JS
		// shim JSON.parses it (same pattern as the radar-sites payload).
		public Task RemapRadarFramesAsync(int newCount, string mappingJson) =>
			_mapView.RunScriptAsync(Call("radarRemap", newCount, mappingJson));

		public Task ClearRadarAsync() =>
			_mapView.RunScriptAsync(Call("clearLevel2Radar"));

		public Task SetRadarOpacityAsync(double opacity) =>
			_mapView.RunScriptAsync(Call("setRadarOpacity", opacity));

		public Task SetRadarProductAsync(string product) =>
			_mapView.RunScriptAsync(Call("setRadarProduct", product));

		public Task SetRadarInspectAsync(bool enabled) =>
			_mapView.RunScriptAsync(Call("setRadarInspect", enabled));

		public Task ShowRadarSitesAsync(string sitesJson) =>
			_mapView.RunScriptAsync(Call("showRadarSites", sitesJson));

		public Task SetSelectedRadarSiteAsync(string? siteId) =>
			_mapView.RunScriptAsync(Call("setSelectedRadarSite", siteId ?? string.Empty));

		public Task SetRadarSweepAsync(double periodSeconds) =>
			_mapView.RunScriptAsync(Call("setRadarSweep", periodSeconds));

		public Task PulseRadarSweepAsync() =>
			_mapView.RunScriptAsync(Call("pulseRadarSweep"));

		public Task SetRadarSitesVisibleAsync(bool visible) =>
			_mapView.RunScriptAsync(Call("setRadarSitesVisible", visible));

		public Task SetRadarSitesStatusAsync(string offlineIdsJson) =>
			_mapView.RunScriptAsync(Call("setRadarSitesStatus", offlineIdsJson));

		public Task SetRadarSiteAccentAsync(string borderColor, string glowColor) =>
			_mapView.RunScriptAsync(Call("setRadarSiteAccent", borderColor, glowColor));

		public Task FlyToAsync(double longitude, double latitude, double zoom) =>
			_mapView.RunScriptAsync(Call("flyTo", longitude, latitude, zoom));

		public Task ShowUserLocationAsync(double longitude, double latitude, string label) =>
			_mapView.RunScriptAsync(Call("showUserLocation", longitude, latitude, label));

		public Task ClearUserLocationAsync() =>
			_mapView.RunScriptAsync(Call("clearUserLocation"));

		public Task ShowDowFrameAsync(string url) =>
			_mapView.RunScriptAsync(Call("showDowFrame", url));

		public Task ClearDowFrameAsync() =>
			_mapView.RunScriptAsync(Call("clearDowFrame"));

		// Builds a "window.fn(a,b,c);" call string, formatting each argument for JS:
		// doubles in invariant culture, bools lowercased, strings single-quoted. This
		// centralizes the JS string-building (and culture handling) for every command.
		private static string Call(string function, params object[] args)
		{
			var rendered = string.Join(",", args.Select(FormatArg));
			return $"window.{function}({rendered});";
		}

		private static string FormatArg(object arg) => arg switch
		{
			double d => d.ToString(CultureInfo.InvariantCulture),
			bool b => b ? "true" : "false",
			string s => $"'{s}'",
			_ => arg?.ToString() ?? "null"
		};
	}
}
