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

		/// <summary>Speculatively builds velocity geometry for the loaded loop in the background (before
		/// the user selects the Velocity product), so a later switch to Velocity is instant. Host calls
		/// this once the reflectivity loop has finished rendering.</summary>
		Task PrefetchRadarVelocityAsync();

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
		/// Stops/removes the selected site's radar sweep (call with <paramref name="periodSeconds"/>
		/// &lt;= 0 on clear / entering replay). The sweep is a one-shot pulse now — see
		/// <see cref="PulseRadarSweepAsync"/> to fire one on a new frame.
		/// </summary>
		Task SetRadarSweepAsync(double periodSeconds);

		/// <summary>
		/// Fires ONE radar-sweep pulse (arm + trailing afterglow, one revolution then hides) — called
		/// when a genuinely-new frame lands, as a "fresh data arrived" cue. The range ring stays up.
		/// </summary>
		Task PulseRadarSweepAsync();

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

		/// <summary>
		/// Sets the accent color driving the "available" site-marker status halo, so it matches the
		/// OS theme accent (like the OverlayBar's accent drop-shadow). <paramref name="borderColor"/>
		/// is a CSS color for the ring; <paramref name="glowColor"/> a CSS color for its soft glow.
		/// </summary>
		Task SetRadarSiteAccentAsync(string borderColor, string glowColor);

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
