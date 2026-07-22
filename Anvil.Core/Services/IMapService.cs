using System.Threading.Tasks;
using Anvil.Models;

namespace Anvil.Services
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

		/// <summary>Sets the rendered radar moment by its product id (e.g. "reflectivity", "velocity",
		/// "cc") — one of the ids in the JS registry (radar-products.js) / <c>RadarProductOptions</c>.</summary>
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

		/// <summary>
		/// Sets the overall opacity (0-1) of the watch polygons. Scales both the faint fill and the bold
		/// outline together, so the slider fades the whole overlay (1 = the default look).
		/// </summary>
		Task SetWatchesOpacityAsync(double opacity);

		/// <summary>
		/// Tells the page where to load the cached storm-based warning GeoJSON from. The page fetches it
		/// lazily (only when warnings are shown) and re-fetches on each refresh push.
		/// </summary>
		Task SetWarningSourceAsync(string url);

		/// <summary>Shows or hides the storm-based warning polygons (Tornado / Severe Thunderstorm
		/// Warnings). These sit above the watch boxes.</summary>
		Task SetWarningsVisibleAsync(bool visible);

		/// <summary>
		/// Sets the overall opacity (0-1) of the warning polygons. Scales both the faint fill and the bold
		/// outline together, so the slider fades the whole overlay (1 = the default look).
		/// </summary>
		Task SetWarningsOpacityAsync(double opacity);

		/// <summary>
		/// Tells the page where to load the cached storm-report GeoJSON (Tornado / Wind / Hail points) from.
		/// The page fetches it with no-store (today's file grows through the day) and re-renders.
		/// </summary>
		Task SetStormReportsSourceAsync(string url);

		/// <summary>Shows the storm-report dots by type — each flag toggles that type's layer independently
		/// (all false hides the overlay without tearing down the source).</summary>
		Task SetStormReportKindsAsync(bool tornado, bool wind, bool hail);

		/// <summary>Sets the overall opacity (0-1) of the storm-report dots.</summary>
		Task SetStormReportsOpacityAsync(double opacity);

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
		/// Shows or hides just the research/test radar markers (e.g. KCRI) — the "Show Research
		/// Radars" toggle. Off by default; operational markers and any active loop are unaffected.
		/// </summary>
		Task SetResearchRadarsVisibleAsync(bool visible);

		/// <summary>
		/// Shows or hides just the Terminal Doppler Weather Radar markers (the FAA `T***` network) —
		/// the "Show TDWRs" toggle. Off by default; operational markers and any active loop are unaffected.
		/// </summary>
		Task SetTdwrsVisibleAsync(bool visible);

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

		// ── Dev-only: velocity-dealias validation harness (see RadarValidationViewModel) ──
		// The scorer replays a fixed corpus through the real decode/dealias path and reports each
		// volume's over-unfold ratio. Because the async decode's Promise can't be awaited through
		// ExecuteScriptAsync, the VM starts the run then polls a JS progress global (like the site
		// sweep polls RadarDiagnostics), rather than routing a message back.

		/// <summary>Starts a validation run over the given corpus. <paramref name="entriesJson"/> is a JSON
		/// array of <c>{ id, url, lat, lon }</c>; the WebView decodes each volume (forcing the velocity
		/// dealias build) and accumulates results into its <c>window.__anvilValidation</c> global.</summary>
		Task StartRadarValidationAsync(string entriesJson);

		/// <summary>Reads back the current run's progress global as JSON (<c>{ total, done, finished,
		/// results:[{id, gatesOver, gatesTotal, ratio, error}] }</c>), or the literal <c>null</c> before a
		/// run starts. Polled until <c>finished</c>.</summary>
		Task<string> PollRadarValidationAsync();

		/// <summary>Signals the in-flight validation run to stop after the current volume.</summary>
		Task CancelRadarValidationAsync();
	}
}
