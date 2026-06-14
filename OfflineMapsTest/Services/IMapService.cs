using System.Threading.Tasks;
using OfflineMapsTest.Models;

namespace OfflineMapsTest.Services
{
	/// <summary>
	/// Application-level operations against the map. Talks to it only through
	/// <see cref="IMapView"/>; has no knowledge of WebView2 or the UI.
	/// </summary>
	public interface IMapService
	{
		/// <summary>Applies the given style to the map (preserves the current camera).</summary>
		Task ApplyStyleAsync(MapStyle style);

		/// <summary>
		/// Shows the given SPC outlook product on the map (adds/replaces a GeoJSON
		/// source + fill/line layers loaded from the product's local cache URL).
		/// </summary>
		Task ShowOutlookAsync(SpcOutlookProduct product);

		/// <summary>Removes the SPC outlook overlay from the map, if any.</summary>
		Task ClearOutlookAsync();

		/// <summary>
		/// Sets the fill opacity (0-1) of the outlook polygons. The outlines are
		/// unaffected, so the basemap reads through the fill.
		/// </summary>
		Task SetOutlookOpacityAsync(double opacity);

		/// <summary>Starts a new radar loop for the site (clears any existing frames).</summary>
		Task BeginRadarLoopAsync(RadarSite site);

		/// <summary>
		/// Adds a cached volume as frame <paramref name="index"/> of the current loop; the
		/// WebView fetches + decodes it off-thread and posts back when the frame is ready.
		/// </summary>
		Task AddRadarFrameAsync(string localUrl, int index);

		/// <summary>Shows the loop frame at <paramref name="index"/>.</summary>
		Task ShowRadarFrameAsync(int index);

		/// <summary>Removes the radar layer and clears the loop.</summary>
		Task ClearRadarAsync();

		/// <summary>Sets the radar layer opacity (0-1).</summary>
		Task SetRadarOpacityAsync(double opacity);

		/// <summary>
		/// Provides the radar sites to the map as clickable on-map markers. JSON is an array
		/// of <c>{ id, name, lng, lat }</c>.
		/// </summary>
		Task ShowRadarSitesAsync(string sitesJson);

		/// <summary>Highlights the selected site marker (empty clears the highlight).</summary>
		Task SetSelectedRadarSiteAsync(string? siteId);

		/// <summary>Animates the map to the given center and zoom.</summary>
		Task FlyToAsync(double longitude, double latitude, double zoom);
	}
}
