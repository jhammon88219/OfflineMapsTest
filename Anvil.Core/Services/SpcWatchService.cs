using System;
using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Anvil.Services
{
	/// <summary>
	/// Default <see cref="ISpcWatchService"/>. Downloads the active Tornado / Severe Thunderstorm
	/// Watch polygons and caches them on disk as GeoJSON; no WebView2 here — MainWindow maps the
	/// cache folder to the "spcwatches" virtual host so the page can fetch
	/// https://spcwatches/watches.geojson.
	///
	/// Source: the NWS WWA event-driven map service (`watch_warn_adv` MapServer, the
	/// `WatchesWarnings` layer 1). It serves the **official county-aggregated** watch geometry — the
	/// polygon is the union of the watch's counties, so it FOLLOWS COUNTY LINES (matching RadarScope)
	/// rather than the older SPC parallelogram box. We query only watches (`sig='A'`) of the two
	/// convective phenomena (`phenom` TO = tornado, SV = severe thunderstorm), as GeoJSON. The
	/// service is current-events-only, so everything returned is active — no client-side expiry
	/// filtering needed. Each feature carries `prod_type` (label), `phenom` (TO/SV — the page colors
	/// by this), and `expiration`.
	/// </summary>
	public sealed class SpcWatchService : ISpcWatchService
	{
		/// <summary>WebView virtual host the cached file is served under (shared contract; the
		/// view owns the actual mapping).</summary>
		public const string CacheHostName = "spcwatches";
		private const string CacheFileName = "watches.geojson";

		// WWA MapServer, layer 1 = "WatchesWarnings" (county-aggregated polygons), filtered to convective
		// WATCHES (sig 'A'; phenom TO/SV), WGS84 (outSR=4326). Split into a base + format so we can also
		// ask for JUST the count to corroborate a suspicious empty GeoJSON — see the RefreshAsync guard.
		private const string QueryBase =
			"https://mapservices.weather.noaa.gov/eventdriven/rest/services/WWA/watch_warn_adv/MapServer/1/query" +
			"?where=sig%3D%27A%27%20AND%20%28phenom%3D%27TO%27%20OR%20phenom%3D%27SV%27%29" +
			"&outFields=prod_type%2Cphenom%2Cexpiration&returnGeometry=true&outSR=4326";
		private const string GeoJsonUrl = QueryBase + "&f=geojson";
		private const string CountUrl = QueryBase + "&returnCountOnly=true&f=json";

		private readonly HttpClient _http;

		public SpcWatchService()
		{
			CacheDirectory = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"Anvil", "SpcWatches");
			Directory.CreateDirectory(CacheDirectory);

			_http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
			_http.DefaultRequestHeaders.UserAgent.ParseAdd("Anvil/1.0");
			// ⚠️ MUST send an Accept header — this NOAA endpoint (Akamai-fronted) caches keyed on Accept
			// (Vary: Accept). HttpClient sends none by default, landing in a rarely-populated cache
			// partition that can serve a stale EMPTY FeatureCollection for a whole TTL after an origin
			// republish, while browsers (Accept: */*) get real data. See WarningService for the full story.
			_http.DefaultRequestHeaders.Accept.ParseAdd("*/*");
		}

		public string CacheDirectory { get; }

		public string WatchesUrl => $"https://{CacheHostName}/{CacheFileName}";

		public async Task<SpcWatchFetchResult> RefreshAsync(CancellationToken cancellationToken = default)
		{
			var cacheFile = Path.Combine(CacheDirectory, CacheFileName);
			var cacheExists = File.Exists(cacheFile);

			try
			{
				var json = await _http.GetStringAsync(GeoJsonUrl, cancellationToken);

				// Only cache a real FeatureCollection. An ArcGIS error object lacks a features array; in
				// that case keep the last-known-good cache instead of blanking it.
				if (!TryGetFeatureCount(json, out var count))
				{
					return Failed(cacheExists, "Response was not a GeoJSON FeatureCollection.");
				}

				// ⚠️ An EMPTY GeoJSON is SUSPECT, not trusted — this event-driven origin intermittently
				// emits a spurious empty set while the count endpoint still reports active features.
				// Corroborate before caching an empty (which would blank the map): only accept it if the
				// lighter count endpoint AGREES the set is really zero. See WarningService for the story.
				if (count == 0 && await RemoteCountAsync(cancellationToken) is > 0)
				{
					return Failed(cacheExists, "Empty GeoJSON contradicted by a non-zero count — kept last-known-good.");
				}

				// Write to a temp file then move over the real one, so a partial/failed write never
				// blanks the last-known-good cache.
				var temp = cacheFile + ".tmp";
				await File.WriteAllTextAsync(temp, json, cancellationToken);
				File.Move(temp, cacheFile, overwrite: true);

				return new SpcWatchFetchResult(SpcWatchFetchStatus.Updated, count);
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				// Never throw to the caller: keep last-known-good on disk and report it.
				return Failed(cacheExists, ex.Message);
			}
		}

		// Asks the server for JUST the matching feature count (returnCountOnly, Esri JSON — a different,
		// lighter code path than the GeoJSON export). Used to sanity-check a suspiciously-empty GeoJSON.
		// Returns the count, or -1 if the check itself failed (so "unknown" doesn't block accepting empty).
		private async Task<int> RemoteCountAsync(CancellationToken cancellationToken)
		{
			try
			{
				var json = await _http.GetStringAsync(CountUrl, cancellationToken);
				if (JsonNode.Parse(json)?["count"] is JsonValue v && v.TryGetValue<int>(out var n))
				{
					return n;
				}
			}
			catch
			{
				// Count check failed — fall through to -1 ("unknown"); the caller won't block on it.
			}
			return -1;
		}

		private static SpcWatchFetchResult Failed(bool cacheExists, string message) =>
			new(cacheExists ? SpcWatchFetchStatus.FailedCacheKept : SpcWatchFetchStatus.FailedNoCache,
				Message: message);

		// Confirms the body is a GeoJSON FeatureCollection and returns its feature count. A missing
		// "features" array (e.g. an ArcGIS {"error":...} object) returns false.
		private static bool TryGetFeatureCount(string geoJson, out int count)
		{
			count = 0;
			try
			{
				if (JsonNode.Parse(geoJson)?["features"] is JsonArray features)
				{
					count = features.Count;
					return true;
				}
			}
			catch
			{
				// Malformed JSON — treat as a failed fetch (keep last-known-good).
			}
			return false;
		}
	}
}
