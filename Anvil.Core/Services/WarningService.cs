using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Anvil.Services
{
	/// <summary>
	/// Default <see cref="IWarningService"/>. Maintains the active, storm-based NWS Tornado / Severe
	/// Thunderstorm WARNING polygons and caches them on disk as GeoJSON; no WebView2 here — MainWindow
	/// maps the cache folder to the "warnings" virtual host so the page can fetch
	/// https://warnings/warnings.geojson.
	///
	/// ⚠️ SOURCE = the authoritative NWS CAP alerts API (`api.weather.gov/alerts/active`), NOT the WWA
	/// mapservice. We switched after catching the WWA `watch_warn_adv` service reporting ZERO active
	/// warnings while 24 were really out (its wrong-empty episodes were the "everything disappeared"
	/// bug, and same-source corroboration couldn't detect them). CAP is authoritative, stable, and its
	/// active feed already carries the storm-based polygon. We query the two convective warning events
	/// (Tornado / Severe Thunderstorm) and TRANSFORM each alert into our render schema — `phenom` (TO/SV,
	/// which warnings.js colors by), `cap_id` (the CAP URN = our merge key), `expiration`, `prod_type`.
	///
	/// NORTH-STAR CHECK: every cycle we also fetch the WWA mapservice's id set as an independent
	/// cross-check and reconcile displayed-vs-CAP-vs-WWA into <see cref="WarningsHealthLog"/> (persisted
	/// to the cache folder), so divergence between what NWS has and what we show is diagnosable after
	/// the fact.
	///
	/// ROBUSTNESS: even the authoritative source can blink, so we DON'T replace the display wholesale —
	/// we keep an in-memory set keyed by `cap_id`, MERGE each fetch in (<see cref="ApplyFetch"/>), and
	/// only drop a warning when it's genuinely gone (an authoritative complete snapshot omits it, a
	/// CONFIRMED all-clear, or past its `expiration`). A bad fetch can't lose active warnings.
	/// </summary>
	public sealed class WarningService : IWarningService
	{
		/// <summary>WebView virtual host the cached file is served under (shared contract; the
		/// view owns the actual mapping).</summary>
		public const string CacheHostName = "warnings";
		private const string CacheFileName = "warnings.geojson";

		// PRIMARY / render source: the authoritative CAP active-alerts feed, filtered to the two
		// convective warning events. Returns a geo+json FeatureCollection with storm-based polygons; each
		// alert's properties.id is the CAP URN (== WWA's cap_id). Fetched with Accept: application/geo+json.
		private const string CapUrl =
			"https://api.weather.gov/alerts/active?event=Tornado%20Warning,Severe%20Thunderstorm%20Warning";

		// CROSS-CHECK only (never rendered): the WWA mapservice id set for the same warnings, so the
		// health log can compare the two independent NWS systems. cap_id, no geometry (lightweight).
		private const string WwaCrossCheckUrl =
			"https://mapservices.weather.noaa.gov/eventdriven/rest/services/WWA/watch_warn_adv/MapServer/1/query" +
			"?where=sig%3D%27W%27%20AND%20%28phenom%3D%27TO%27%20OR%20phenom%3D%27SV%27%29" +
			"&outFields=cap_id&returnGeometry=false&outSR=4326&f=geojson";

		// A warning may linger this long past its stated expiration before we prune it (clock-skew grace;
		// always the SAFE direction — showing a just-expired polygon briefly beats dropping a live one).
		private static readonly TimeSpan ExpirationGrace = TimeSpan.FromMinutes(2);
		// If a feature arrives with no parseable expiration, assume this lifetime so it can't linger forever.
		private static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(90);
		// Clearing the whole set to zero requires this many CONSECUTIVE "empty" cycles, so a single blink
		// of the authoritative source can't wipe active warnings.
		private const int EmptyConfirmations = 2;

		private readonly HttpClient _http;
		private readonly WarningsHealthLog _health;

		// The current active warning set, keyed by cap_id. Mutated only from the (serial) refresh loop.
		private readonly Dictionary<string, ActiveWarning> _active = new();
		private int _consecutiveEmpty;
		private string _lastClassification = "init";

		private readonly record struct ActiveWarning(JsonNode Feature, DateTimeOffset Expires);

		public WarningService()
		{
			CacheDirectory = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"Anvil", "Warnings");
			Directory.CreateDirectory(CacheDirectory);

			_http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
			// A descriptive User-Agent is REQUIRED by api.weather.gov (it 403s a blank UA) and harmless to
			// the WWA service. Accept is set per-request (geo+json for CAP; */* for the Akamai-fronted WWA
			// cross-check, which caches keyed on Accept — a no-Accept request can be served a stale empty).
			_http.DefaultRequestHeaders.UserAgent.ParseAdd("Anvil/1.0 (severe-weather app)");

			_health = new WarningsHealthLog(CacheDirectory);
		}

		public string CacheDirectory { get; }

		public string WarningsUrl => $"https://{CacheHostName}/{CacheFileName}";

		public async Task<WarningFetchResult> RefreshAsync(CancellationToken cancellationToken = default)
		{
			var cacheFile = Path.Combine(CacheDirectory, CacheFileName);
			var cacheExists = File.Exists(cacheFile);

			string capJson;
			try
			{
				capJson = await GetAsync(CapUrl, "application/geo+json", cancellationToken);
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				// Network failure — leave the set + cache untouched (display persists); log it as a failure.
				WriteFailedHealth(ex.Message);
				return Failed(cacheExists, ex.Message);
			}

			if (!TryTransformCap(capJson, out var features, out var primaryIds))
			{
				WriteFailedHealth("CAP response was not a GeoJSON FeatureCollection.");
				return Failed(cacheExists, "CAP response was not a GeoJSON FeatureCollection.");
			}

			// CAP's active feed is the COMPLETE authoritative set (one page comfortably holds every active
			// warning nationwide), so its own feature count is the authoritative count for the merge.
			ApplyFetch(features, features.Count, DateTimeOffset.UtcNow);

			// Write the merged set atomically (temp then move) so a partial write never blanks the cache.
			await WriteCollectionAsync(cacheFile, cancellationToken);

			// NORTH-STAR CHECK: independent WWA cross-check (best-effort), then reconcile + persist.
			var crossIds = await WwaCrossCheckIdsAsync(cancellationToken);
			_health.Write(WarningsHealthLog.Reconcile(
				DateTimeOffset.Now, _active.Keys, primaryIds, crossIds, _lastClassification));

			var (tornado, severe) = CountByPhenom();
			return new WarningFetchResult(WarningFetchStatus.Updated, _active.Count, tornado, severe);
		}

		// Tallies the current active set by phenom for the UI readout (TO = tornado, SV = severe t-storm).
		private (int Tornado, int Severe) CountByPhenom()
		{
			int tornado = 0, severe = 0;
			foreach (var w in _active.Values)
			{
				var phenom = Str(w.Feature["properties"]?["phenom"]);
				if (phenom == "TO") { tornado++; }
				else if (phenom == "SV") { severe++; }
			}
			return (tornado, severe);
		}

		/// <summary>
		/// The robustness core: folds one fetch (its <paramref name="fetched"/> features and the
		/// <paramref name="authoritative"/> count, -1 = unknown) into the running active set, using
		/// <paramref name="now"/> as the clock. Kept HTTP-free and <c>internal</c> so it can be unit-tested
		/// with scripted empty/partial/full/expired scenarios. Returns the resulting active count.
		/// </summary>
		internal int ApplyFetch(IReadOnlyList<JsonNode> fetched, int authoritative, DateTimeOffset now)
		{
			// 1) MERGE: add/update every fetched feature by id. This alone means a partial fetch never
			//    loses the warnings it omitted — they stay from a previous cycle.
			var fetchedIds = new HashSet<string>();
			foreach (var feature in fetched)
			{
				var id = FeatureId(feature);
				fetchedIds.Add(id);
				_active[id] = new ActiveWarning(feature.DeepClone(), FeatureExpiry(feature, now));
			}

			// 2) RECONCILE: only prune based on absence when we trust this fetch as a COMPLETE snapshot.
			var complete = authoritative >= 0 && fetched.Count == authoritative;
			var bothEmpty = fetched.Count == 0 && authoritative == 0;
			var dropped = 0;
			if (bothEmpty)
			{
				// "Nothing active" — require repeated agreement before wiping, so a one-cycle blink of the
				// source can't clear a live set.
				if (++_consecutiveEmpty >= EmptyConfirmations)
				{
					dropped = _active.Count;
					_active.Clear();
					_lastClassification = "cleared";
				}
				else
				{
					_lastClassification = "blink-held";
				}
			}
			else
			{
				_consecutiveEmpty = 0;
				if (complete && fetched.Count > 0)
				{
					// Trustworthy full snapshot: drop anything the source no longer lists (a cancelled or
					// expired warning) promptly, instead of waiting for its expiration.
					foreach (var goneKey in _active.Keys.Where(k => !fetchedIds.Contains(k)).ToList())
					{
						_active.Remove(goneKey);
						dropped++;
					}
					_lastClassification = $"complete(dropped={dropped})";
				}
				else
				{
					// Partial / spurious-empty / count-unknown: keep the union; step 3 caps staleness.
					_lastClassification = fetched.Count == 0 ? "spurious-empty-held" : "partial-held";
				}
			}

			// 3) EXPIRE: hard cap on staleness — a warning past its own expiration (or a cancelled one that
			//    lingered because we only ever saw partial fetches) is dropped regardless.
			foreach (var kv in _active.Where(kv => now > kv.Value.Expires + ExpirationGrace).ToList())
			{
				_active.Remove(kv.Key);
			}

			return _active.Count;
		}

		/// <summary>Test hook: the ids currently in the active set.</summary>
		internal IReadOnlyCollection<string> ActiveIds => _active.Keys.ToList();

		/// <summary>Test hook: the merge classification recorded on the last <see cref="ApplyFetch"/>.</summary>
		internal string LastClassification => _lastClassification;

		/// <summary>
		/// Transforms a CAP active-alerts geo+json body into our render-schema features (keyed by cap_id,
		/// carrying phenom/expiration/prod_type + the storm-based geometry) and collects their ids. Skips
		/// alerts without a polygon or a recognized event. Returns false if the body isn't a
		/// FeatureCollection at all (caller keeps last-known-good). Pure + <c>internal</c> for unit tests.
		/// </summary>
		internal static bool TryTransformCap(string capJson, out List<JsonNode> features, out List<string> ids)
		{
			features = new List<JsonNode>();
			ids = new List<string>();
			try
			{
				if (JsonNode.Parse(capJson)?["features"] is not JsonArray arr)
				{
					return false;
				}
				foreach (var f in arr)
				{
					var props = f?["properties"];
					var geom = f?["geometry"];
					if (props is null || geom is null)
					{
						continue; // no polygon → can't render (rare for storm-based warnings)
					}
					var evt = Str(props["event"]);
					var phenom = PhenomForEvent(evt);
					var id = Str(props["id"]);
					if (phenom is null || string.IsNullOrEmpty(id))
					{
						continue; // not one of our two events, or no id to key on
					}
					features.Add(new JsonObject
					{
						["type"] = "Feature",
						["geometry"] = geom.DeepClone(),
						["properties"] = new JsonObject
						{
							["phenom"] = phenom,          // TO/SV — warnings.js colors by this
							["prod_type"] = evt,          // human label
							["cap_id"] = id,              // CAP URN = merge key (matches WWA cap_id)
							["expiration"] = Str(props["expires"]),
						},
					});
					ids.Add(id);
				}
				return true;
			}
			catch
			{
				return false;
			}
		}

		// Maps a CAP event name to our phenom code (the only two we query).
		private static string? PhenomForEvent(string evt) =>
			evt.StartsWith("Tornado", StringComparison.OrdinalIgnoreCase) ? "TO" :
			evt.StartsWith("Severe Thunderstorm", StringComparison.OrdinalIgnoreCase) ? "SV" :
			null;

		// Serializes the current active set as a GeoJSON FeatureCollection and moves it over the cache
		// file. Each stored feature is deep-cloned again so the in-memory copies stay parentless (a JsonNode
		// can only be attached to one parent) and reusable next cycle.
		private async Task WriteCollectionAsync(string cacheFile, CancellationToken cancellationToken)
		{
			var features = new JsonArray();
			foreach (var w in _active.Values)
			{
				features.Add(w.Feature.DeepClone());
			}
			var collection = new JsonObject { ["type"] = "FeatureCollection", ["features"] = features };

			var temp = cacheFile + ".tmp";
			await File.WriteAllTextAsync(temp, collection.ToJsonString(), cancellationToken);
			File.Move(temp, cacheFile, overwrite: true);
		}

		// Fetches the WWA mapservice's active-warning cap_id set for the north-star cross-check. Best-effort:
		// returns null if it fails, so a WWA hiccup never affects the rendered set — only the health log.
		private async Task<IReadOnlyCollection<string>?> WwaCrossCheckIdsAsync(CancellationToken cancellationToken)
		{
			try
			{
				// */* + cache-bust: the WWA endpoint is Akamai-fronted (caches keyed on Accept; a stale edge
				// copy otherwise). ArcGIS ignores the unknown cache-buster.
				var json = await GetAsync(Bust(WwaCrossCheckUrl), "*/*", cancellationToken);
				if (JsonNode.Parse(json)?["features"] is JsonArray arr)
				{
					var set = new HashSet<string>();
					foreach (var f in arr)
					{
						var id = Str(f?["properties"]?["cap_id"]);
						if (!string.IsNullOrEmpty(id)) { set.Add(id); }
					}
					return set;
				}
			}
			catch
			{
				// Cross-check unavailable this cycle — reported as CrossCheckCount = -1 in the health log.
			}
			return null;
		}

		// GET with an explicit Accept header (the two sources want different ones; see the ctor note).
		private async Task<string> GetAsync(string url, string accept, CancellationToken cancellationToken)
		{
			using var req = new HttpRequestMessage(HttpMethod.Get, url);
			req.Headers.Accept.ParseAdd(accept);
			using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cancellationToken);
			resp.EnsureSuccessStatusCode();
			return await resp.Content.ReadAsStringAsync(cancellationToken);
		}

		private void WriteFailedHealth(string note) =>
			_health.Write(new WarningsHealth(
				DateTimeOffset.Now, _active.Count, -1, -1,
				Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
				"fetch-failed", "FETCH_FAILED", note));

		// Appends a per-request cache-buster so a CDN can't serve a stale edge copy for this real-time feed.
		private static string Bust(string url) => url + "&_=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

		// Stable per-warning id: the CAP identifier when present, else a composite of identifying fields.
		private static string FeatureId(JsonNode feature)
		{
			var props = feature["properties"];
			var cap = Str(props?["cap_id"]);
			if (!string.IsNullOrEmpty(cap))
			{
				return cap;
			}
			return string.Join('|', Str(props?["phenom"]), Str(props?["prod_type"]), Str(props?["expiration"]));
		}

		// Parses the feature's stated expiration (ISO-8601 with offset, e.g. 2026-07-21T16:15:00-05:00) to
		// UTC; falls back to a default lifetime from now if it's missing/unparseable.
		private static DateTimeOffset FeatureExpiry(JsonNode feature, DateTimeOffset now)
		{
			var expStr = Str(feature["properties"]?["expiration"]);
			if (!string.IsNullOrEmpty(expStr) &&
				DateTimeOffset.TryParse(expStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exp))
			{
				return exp.ToUniversalTime();
			}
			return now + DefaultLifetime;
		}

		private static string Str(JsonNode? node)
		{
			if (node is JsonValue v)
			{
				return v.TryGetValue<string>(out var s) ? s : v.ToString();
			}
			return node?.ToString() ?? string.Empty;
		}

		private static WarningFetchResult Failed(bool cacheExists, string message) =>
			new(cacheExists ? WarningFetchStatus.FailedCacheKept : WarningFetchStatus.FailedNoCache,
				Message: message);
	}
}
