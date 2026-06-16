using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OfflineMapsTest.Models;

namespace OfflineMapsTest.Services
{
	/// <summary>
	/// Default <see cref="ISpcOutlookService"/>. Downloads SPC outlook GeoJSON with
	/// HttpClient and caches it on disk; <c>SpcOutlookCatalog</c> holds the editable
	/// source-URL table. No WebView2 here — MainWindow maps the cache folder to the
	/// "spcoutlooks" virtual host so the page can fetch https://spcoutlooks/&lt;id&gt;.geojson.
	/// </summary>
	public sealed class SpcOutlookService : ISpcOutlookService
	{
		/// <summary>WebView virtual host the cached files are served under. The page
		/// loads each product from $"https://{CacheHostName}/{product.CacheFileName}".
		/// MainWindow owns the actual mapping; this constant is the shared contract.</summary>
		public const string CacheHostName = "spcoutlooks";

		private readonly HttpClient _http;
		private readonly IReadOnlyList<SpcOutlookProduct> _products;

		public SpcOutlookService()
		{
			// Per-user cache folder; works packaged or unpackaged. Created up front so
			// the virtual-host mapping has a real folder to point at on first run.
			CacheDirectory = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"OfflineMapsTest", "SpcOutlooks");
			Directory.CreateDirectory(CacheDirectory);

			_http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
			// Some NOAA/IEM endpoints reject requests without a User-Agent.
			_http.DefaultRequestHeaders.UserAgent.ParseAdd("OfflineMapsTest/1.0");

			// Project the editable endpoint table into the public product list, adding
			// the stable cache filename and local URL for each.
			_products = SpcOutlookCatalog.All
				.Select(e => new SpcOutlookProduct(
					e.Id, e.Day, e.Type, e.DisplayName,
					CacheFileName: e.Id + ".geojson",
					LocalUrl: $"https://{CacheHostName}/{e.Id}.geojson"))
				.ToList();
		}

		public string CacheDirectory { get; }

		public IReadOnlyList<SpcOutlookProduct> Products => _products;

		public IReadOnlyList<int> AvailableDays =>
			_products.Select(p => p.Day).Distinct().OrderBy(d => d).ToList();

		public IReadOnlyList<SpcOutlookProduct> GetProductsForDay(int day) =>
			_products.Where(p => p.Day == day).ToList();

		public SpcOutlookProduct? Resolve(int day, SpcOutlookType type) =>
			_products.FirstOrDefault(p => p.Day == day && p.Type == type);

		public SpcOutlookTimes? GetTimesForProduct(SpcOutlookProduct product)
		{
			var cacheFile = Path.Combine(CacheDirectory, product.CacheFileName);
			if (!File.Exists(cacheFile))
			{
				return null;
			}

			try
			{
				using var doc = JsonDocument.Parse(File.ReadAllText(cacheFile));
				if (!doc.RootElement.TryGetProperty("features", out var features) ||
					features.ValueKind != JsonValueKind.Array ||
					features.GetArrayLength() == 0)
				{
					return null; // no risk areas -> no times to show
				}

				if (!features[0].TryGetProperty("properties", out var props))
				{
					return null;
				}

				// Convective products expose clean ISO fields; fire-weather (ArcGIS) only
				// carries lowercase yyyyMMddHHmm valid/expire (UTC) and no issue time.
				var issued = ReadIso(props, "ISSUE_ISO");
				var valid = ReadIso(props, "VALID_ISO") ?? ReadStamp(props, "valid");
				var expire = ReadIso(props, "EXPIRE_ISO") ?? ReadStamp(props, "expire");

				return (issued is null && valid is null && expire is null)
					? null
					: new SpcOutlookTimes(issued, valid, expire);
			}
			catch
			{
				return null; // malformed cache is non-fatal; just show no times
			}
		}

		private static DateTimeOffset? ReadIso(JsonElement props, string name) =>
			props.TryGetProperty(name, out var el) &&
			el.ValueKind == JsonValueKind.String &&
			DateTimeOffset.TryParse(el.GetString(), CultureInfo.InvariantCulture,
				DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt)
				? dt
				: null;

		private static DateTimeOffset? ReadStamp(JsonElement props, string name) =>
			props.TryGetProperty(name, out var el) &&
			el.ValueKind == JsonValueKind.String &&
			DateTimeOffset.TryParseExact(el.GetString(), "yyyyMMddHHmm",
				CultureInfo.InvariantCulture,
				DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt)
				? dt
				: null;

		public async Task<IReadOnlyList<SpcOutlookFetchResult>> RefreshAllAsync(CancellationToken cancellationToken = default)
		{
			// Sequential keeps it gentle on NOAA and easy to reason about. A future
			// scheduled refresh (timer/background task) would call this same method —
			// hook the scheduler in at the call site, not here.
			var results = new List<SpcOutlookFetchResult>(SpcOutlookCatalog.All.Count);
			foreach (var endpoint in SpcOutlookCatalog.All)
			{
				cancellationToken.ThrowIfCancellationRequested();
				results.Add(await RefreshOneAsync(endpoint, cancellationToken));
			}
			return results;
		}

		private async Task<SpcOutlookFetchResult> RefreshOneAsync(SpcOutlookEndpoint endpoint, CancellationToken ct)
		{
			var cacheFile = Path.Combine(CacheDirectory, endpoint.Id + ".geojson");
			var cacheExists = File.Exists(cacheFile);

			try
			{
				// Try primary, then fallback (if any). 304 short-circuits to NotModified.
				var outcome = await TryFetchAsync(endpoint.PrimaryUrl, cacheFile, ct);
				if (outcome is null && endpoint.FallbackUrl is not null)
				{
					outcome = await TryFetchAsync(endpoint.FallbackUrl, cacheFile, ct);
				}

				return outcome switch
				{
					FetchOutcome.Updated => new SpcOutlookFetchResult(endpoint.Id, SpcOutlookFetchStatus.Updated),
					FetchOutcome.NotModified => new SpcOutlookFetchResult(endpoint.Id, SpcOutlookFetchStatus.NotModified),
					_ => Failed(endpoint.Id, cacheExists, "All sources failed."),
				};
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				// Never let one product abort the batch; keep last-known-good on disk.
				return Failed(endpoint.Id, cacheExists, ex.Message);
			}
		}

		private static SpcOutlookFetchResult Failed(string id, bool cacheExists, string message) =>
			new(id, cacheExists ? SpcOutlookFetchStatus.FailedCacheKept : SpcOutlookFetchStatus.FailedNoCache, message);

		private enum FetchOutcome { Updated, NotModified }

		// Returns the outcome on success/304, or null if THIS url failed (so the caller
		// can try a fallback). Writes the cache file atomically and persists validators.
		private async Task<FetchOutcome?> TryFetchAsync(string url, string cacheFile, CancellationToken ct)
		{
			using var request = new HttpRequestMessage(HttpMethod.Get, url);

			// Conditional GET from persisted ETag / Last-Modified, but only if we still
			// have the cache file they describe.
			var haveCache = File.Exists(cacheFile);
			var validators = haveCache ? ReadValidators(cacheFile) : default;
			if (validators.ETag is not null)
			{
				request.Headers.IfNoneMatch.ParseAdd(validators.ETag);
			}
			if (validators.LastModified is not null &&
				DateTimeOffset.TryParse(validators.LastModified, out var lm))
			{
				request.Headers.IfModifiedSince = lm;
			}

			using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

			if (response.StatusCode == HttpStatusCode.NotModified)
			{
				return FetchOutcome.NotModified;
			}
			if (!response.IsSuccessStatusCode)
			{
				return null; // let the caller try a fallback
			}

			// Write to a temp file then move over the real one, so a partial or failed
			// download never blanks the last-known-good cache.
			var temp = cacheFile + ".tmp";
			await using (var src = await response.Content.ReadAsStreamAsync(ct))
			await using (var dst = File.Create(temp))
			{
				await src.CopyToAsync(dst, ct);
			}
			File.Move(temp, cacheFile, overwrite: true);

			WriteValidators(cacheFile, new Validators(
				ETag: response.Headers.ETag?.Tag,
				LastModified: response.Content.Headers.LastModified?.ToString("O")));

			return FetchOutcome.Updated;
		}

		// Validators are stored next to each cache file as "<id>.geojson.meta".
		private readonly record struct Validators(string? ETag, string? LastModified);

		private static Validators ReadValidators(string cacheFile)
		{
			var meta = cacheFile + ".meta";
			if (!File.Exists(meta))
			{
				return default;
			}
			try
			{
				return JsonSerializer.Deserialize<Validators>(File.ReadAllText(meta));
			}
			catch
			{
				return default;
			}
		}

		private static void WriteValidators(string cacheFile, Validators validators)
		{
			try
			{
				File.WriteAllText(cacheFile + ".meta", JsonSerializer.Serialize(validators));
			}
			catch
			{
				// Validators are an optimization; failing to persist them is non-fatal.
			}
		}
	}
}
