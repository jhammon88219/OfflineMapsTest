using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Anvil.Models;

namespace Anvil.Services
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
		/// The freshest scan known for a site (the Radar Site Explorer's detail pane): when it was
		/// collected + the VCP/scan-mode line. Null when the site has no recent data.
		///
		/// Consults BOTH feeds, because they disagree by design: the archive bucket runs ~5-10 min behind
		/// while the near-real-time chunks bucket is ~1-2 min (which is what the loop's live frame shows).
		/// The time is whichever is fresher; the mode comes from the cached archive tilt. Costs a listing +
		/// a range-GET prefix (cached) + a chunks LISTING — no chunk downloads, so it never disturbs the
		/// live-frame cache of a loop running on another site.
		/// </summary>
		Task<RadarScanInfo?> GetLatestScanAsync(RadarSite site, CancellationToken cancellationToken = default);

		/// <summary>
		/// Returns the keys of every volume for the site whose scan time falls within the given UTC
		/// window, oldest-first — the Past Event Viewer's source. Lists each UTC day the window
		/// touches (handles a window that crosses midnight). Listing only; the caller fetches the
		/// volumes it wants via <see cref="EnsureCachedAsync"/>.
		/// </summary>
		Task<IReadOnlyList<string>> GetKeysForWindowAsync(RadarSite site, System.DateTimeOffset startUtc, System.DateTimeOffset endUtc, CancellationToken cancellationToken = default);

		/// <summary>
		/// Ensures ONE TILT of the volume for <paramref name="key"/> is downloaded + extracted to the
		/// cache (reusing it if already present), returning its overlay descriptor, or null if the fetch
		/// failed or the volume has no cut at that tilt.
		///
		/// <para><paramref name="tiltAngle"/> selects the elevation angle in degrees; null means the base
		/// (lowest) tilt — the default view, whose path is unchanged from before tilt selection existed
		/// (a ~5 MB range-GET of the file's leading prefix, where the lowest tilt lives). A HIGHER tilt
		/// isn't at the file start, so it costs a FULL volume download (~10-30 MB) unless
		/// <see cref="PrefetchRawVolumesAsync"/> has already retained the raw volume, in which case it's a
		/// local decompress with no network. Each (volume, tilt) is cached separately, so returning to a
		/// visited tilt is free. Valid angles come from <see cref="RadarVolume.Tilts"/>.</para>
		/// </summary>
		Task<RadarVolume?> EnsureCachedAsync(RadarSite site, string key, float? tiltAngle = null, CancellationToken cancellationToken = default);

		/// <summary>
		/// Speculatively downloads the given volumes IN FULL in the background and retains them, so that
		/// extracting any tilt from them later needs no network — the tilt analogue of velocity prefetch.
		///
		/// <para>It prefetches VOLUMES, not tilts, because one volume download already contains every
		/// tilt: fetching each tilt separately would re-download the same bytes once per tilt (~9× the
		/// network for the same result). The cost is a full download per volume (~10-30 MB, vs the ~5 MB
		/// prefix the base tilt actually needs) even if the user never leaves the base tilt — the same
		/// bargain velocity prefetch makes, so callers should arm it only once the base view is up, and
		/// it runs at a lower concurrency than the live poll so it can't starve what's on screen.</para>
		/// </summary>
		Task PrefetchRawVolumesAsync(RadarSite site, IReadOnlyList<string> keys, CancellationToken cancellationToken = default);

		/// <summary>
		/// Builds the freshest possible single-tilt frame from the near-real-time
		/// <c>unidata-nexrad-level2-chunks</c> bucket: finds the newest (often still in-progress)
		/// volume, assembles its <c>S</c>+<c>I</c> chunks, and extracts one tilt — giving
		/// ~1-2 min latency vs the archive bucket's ~10 min. Returns null if no fresh volume is
		/// available yet or that tilt hasn't finished scanning (caller should fall back to
		/// the newest archive volume). Cached + served from the same virtual host as archive
		/// frames.
		///
		/// <para><paramref name="tiltAngle"/> selects the elevation; null = the 0.5° base. A higher tilt
		/// costs no extra network — the whole in-progress volume's chunks are already downloaded and
		/// decoded, so any tilt in it is extracted from bytes we hold. But only LOW tilts are worth
		/// asking for: the radar scans bottom-up, so a tilt's freshness floor is when the antenna
		/// reaches it (~2-3 min for the bottom few, ~5 min by 8°+ — which is the archive's latency
		/// anyway). Callers cap this via <c>RadarViewModel.LiveTiltCount</c>. Note also that SAILS
		/// re-scans only the BASE tilt, so a higher tilt refreshes once per ~4.5-min volume rather than
		/// every ~1-2 min.</para>
		/// </summary>
		Task<RadarVolume?> GetLiveFrameAsync(RadarSite site, float? tiltAngle = null, CancellationToken cancellationToken = default);

		/// <summary>
		/// Returns the set of site IDs that have data in the archive bucket within the last day —
		/// i.e. the sites this feed can actually show right now. A site NOT in the set is offline
		/// in this feed (no data flowing), like KLIX, even if the radar is physically scanning.
		/// One date-prefix listing per day (today + yesterday); cheap.
		/// </summary>
		Task<IReadOnlyCollection<string>> GetLiveSiteIdsAsync(CancellationToken cancellationToken = default);

		/// <summary>
		/// Returns the site IDs that have any Level II data in the archive bucket over the UTC date(s)
		/// the given window spans — used by the Past Event Viewer to gray out sites that were down or
		/// didn't exist yet on that date. One date-prefix listing per day (the window spans ≤2 UTC days).
		/// </summary>
		Task<IReadOnlyCollection<string>> GetSiteIdsForDateAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken = default);
	}
}
