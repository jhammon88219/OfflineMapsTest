using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OfflineMapsTest.Models;

namespace OfflineMapsTest.Services
{
	/// <summary>
	/// Fetches and caches NEXRAD Level II volumes (lowest-tilt reflectivity, one file per
	/// volume timestamp) from the public AWS archive bucket. All HTTP happens here (C# side,
	/// avoiding the WebView's CORS limits); this service never touches WebView2. MainWindow
	/// maps <see cref="CacheDirectory"/> to a WebView virtual host so the page can read each
	/// cached file and decode it in JS.
	/// </summary>
	public interface ILevel2RadarService
	{
		/// <summary>Absolute path of the on-disk volume cache folder.</summary>
		string CacheDirectory { get; }

		/// <summary>
		/// Returns the keys of the most recent <paramref name="count"/> volumes for the site,
		/// oldest-first, and prunes cached volumes outside that set. Cheap (listing only).
		/// </summary>
		Task<IReadOnlyList<string>> GetRecentKeysAsync(RadarSite site, int count, CancellationToken cancellationToken = default);

		/// <summary>
		/// Returns the keys of every volume for the site whose scan time falls within the given UTC
		/// window, oldest-first — the Past Event Viewer's source. Lists each UTC day the window
		/// touches (handles a window that crosses midnight). Listing only; the caller fetches the
		/// volumes it wants via <see cref="EnsureCachedAsync"/>.
		/// </summary>
		Task<IReadOnlyList<string>> GetKeysForWindowAsync(RadarSite site, System.DateTimeOffset startUtc, System.DateTimeOffset endUtc, CancellationToken cancellationToken = default);

		/// <summary>
		/// Ensures the volume for <paramref name="key"/> is downloaded + lowest-tilt-extracted
		/// to the cache (reusing it if already present), returning its overlay descriptor, or
		/// null if the fetch failed.
		/// </summary>
		Task<RadarVolume?> EnsureCachedAsync(RadarSite site, string key, CancellationToken cancellationToken = default);

		/// <summary>
		/// Builds the freshest possible single-tilt frame from the near-real-time
		/// <c>unidata-nexrad-level2-chunks</c> bucket: finds the newest (often still in-progress)
		/// volume, assembles its <c>S</c>+<c>I</c> chunks, and extracts the lowest tilt — giving
		/// ~1-2 min latency vs the archive bucket's ~10 min. Returns null if no fresh volume is
		/// available yet or its lowest tilt hasn't finished scanning (caller should fall back to
		/// the newest archive volume). Cached + served from the same virtual host as archive
		/// frames.
		/// </summary>
		Task<RadarVolume?> GetLiveFrameAsync(RadarSite site, CancellationToken cancellationToken = default);

		/// <summary>
		/// Returns the set of site IDs that have data in the archive bucket within the last day —
		/// i.e. the sites this feed can actually show right now. A site NOT in the set is offline
		/// in this feed (no data flowing), like KLIX, even if the radar is physically scanning.
		/// One date-prefix listing per day (today + yesterday); cheap.
		/// </summary>
		Task<IReadOnlyCollection<string>> GetLiveSiteIdsAsync(CancellationToken cancellationToken = default);
	}
}
