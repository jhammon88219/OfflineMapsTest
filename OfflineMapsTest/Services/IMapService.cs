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

		/// <summary>
		/// Incrementally refreshes the loop: reindexes the already-decoded frames to a new ordering
		/// (reusing their geometry) instead of rebuilding from scratch, so a periodic reload doesn't
		/// blank the layer or re-decode unchanged volumes. <paramref name="mappingJson"/> is an array
		/// of <c>[fromIndex, toIndex]</c> pairs; the host then adds only the genuinely-new frames.
		/// </summary>
		Task RemapRadarFramesAsync(int newCount, string mappingJson);

		/// <summary>Removes the radar layer and clears the loop.</summary>
		Task ClearRadarAsync();

		/// <summary>Sets the radar layer opacity (0-1).</summary>
		Task SetRadarOpacityAsync(double opacity);

		/// <summary>Sets the rendered radar moment: "reflectivity" or "velocity".</summary>
		Task SetRadarProductAsync(string product);

		/// <summary>
		/// Enables or disables inspect mode (read the value under the cursor). While on, the WebView
		/// shows a value tooltip at the pointer and posts the value for the color-scale marker.
		/// </summary>
		Task SetRadarInspectAsync(bool enabled);

		/// <summary>
		/// Provides the radar sites to the map as clickable on-map markers. JSON is an array
		/// of <c>{ id, name, lng, lat }</c>.
		/// </summary>
		Task ShowRadarSitesAsync(string sitesJson);

		/// <summary>
		/// Tells the page where to load the cached SPC watch-box GeoJSON from. The page fetches it
		/// lazily (only when watches are shown) and re-fetches on each refresh push.
		/// </summary>
		Task SetWatchSourceAsync(string url);

		/// <summary>Shows or hides the SPC watch boxes (Tornado / Severe Thunderstorm Watches).</summary>
		Task SetWatchesVisibleAsync(bool visible);

		/// <summary>Highlights the selected site marker (empty clears the highlight).</summary>
		Task SetSelectedRadarSiteAsync(string? siteId);

		/// <summary>
		/// Phase-locks the selected site's radar-sweep animation to the live-poll cycle: one full
		/// revolution takes <paramref name="periodSeconds"/>, started now, so the arm completes as
		/// the next radar update is due. A value &lt;= 0 returns the sweep to its free-running speed.
		/// </summary>
		Task SetRadarSweepAsync(double periodSeconds);

		/// <summary>
		/// Shows or hides all radar site marker buttons. Independent of the radar layer —
		/// hiding the markers never clears or hides an active radar loop.
		/// </summary>
		Task SetRadarSitesVisibleAsync(bool visible);

		/// <summary>
		/// Marks which site markers are offline (no recent data in the feed). JSON is an array
		/// of site IDs; those markers render in the muted "offline" style.
		/// </summary>
		Task SetRadarSitesStatusAsync(string offlineIdsJson);

		/// <summary>Animates the map to the given center and zoom.</summary>
		Task FlyToAsync(double longitude, double latitude, double zoom);

		/// <summary>
		/// Places (or moves) the user-location marker at the given coordinates. <paramref name="label"/>
		/// is the marker tooltip (e.g. the resolved place name or "Device location").
		/// </summary>
		Task ShowUserLocationAsync(double longitude, double latitude, string label);

		/// <summary>Removes the user-location marker, if any.</summary>
		Task ClearUserLocationAsync();

		/// <summary>
		/// Shows a single curated DOW (mobile-radar) frame from its <c>dowevents</c> host URL, reusing
		/// the radar render pipeline. The WebView fetches the <c>.dow.json</c> and decodes it on the
		/// main thread (one sweep), centred on the truck's position carried in the frame.
		/// </summary>
		Task ShowDowFrameAsync(string url);

		/// <summary>Removes the shown DOW frame (clears the radar layer).</summary>
		Task ClearDowFrameAsync();
	}
}
