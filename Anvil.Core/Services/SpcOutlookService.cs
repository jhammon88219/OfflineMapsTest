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
using Anvil.Models;

namespace Anvil.Services
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
				"Anvil", "SpcOutlooks");
			Directory.CreateDirectory(CacheDirectory);

			_http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
			// Some NOAA/IEM endpoints reject requests without a User-Agent.
			_http.DefaultRequestHeaders.UserAgent.ParseAdd("Anvil/1.0");

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

		// ── Historical outlooks for PastCast (IEM archive; see SpcOutlookColors + PastOutlookViewModel) ──
		// SPC's own site serves only the latest outlook, so historical issuances come from the Iowa
		// Environmental Mesonet API (parses SPC's PTS product; polygons back to ~2002). One convective (C)
		// call returns every category for the issuance; a fire (F) call returns fire. IEM ships no colors,
		// so SpcOutlookColors transforms each into the renderer's fill/stroke/LABEL schema and we cache one
		// file per product. Historical data is immutable → fetch-once, cache-forever (no refresh loop).
		private const string IemOutlookBase = "https://mesonet.agron.iastate.edu/api/1/nws/spc_outlook.geojson";

		// The product types we attempt to extract from a convective issuance (empties are skipped, so
		// attempting all four on a Day-3 issuance that only has Categorical is harmless).
		private static readonly SpcOutlookType[] ConvectiveTypes =
		{
			SpcOutlookType.Categorical, SpcOutlookType.Tornado, SpcOutlookType.Wind, SpcOutlookType.Hail,
			SpcOutlookType.ProbabilisticCombined, // Day 2-3 "ANY SEVERE" (empty on Day 1, skipped)
		};

		/// <summary>Stable cache filename for one historical outlook product (also the basis of its
		/// local URL). Deterministic so the VM can point <see cref="SpcOutlookProduct.LocalUrl"/> at a
		/// file this method wrote.</summary>
		public static string PastCacheName(DateOnly date, int day, int cycle, SpcOutlookType type) =>
			$"past-{date:yyyyMMdd}-d{day}-c{cycle:D2}-{TypeSlug(type)}.geojson";

		/// <summary>The <c>spcoutlooks</c>-host URL a cached historical product is served from.</summary>
		public string PastLocalUrl(DateOnly date, int day, int cycle, SpcOutlookType type) =>
			$"https://{CacheHostName}/{PastCacheName(date, day, cycle, type)}";

		public async Task<PastOutlookResult> EnsurePastOutlookAsync(DateOnly date, int day, int cycle,
			CancellationToken cancellationToken = default)
		{
			var available = new List<SpcOutlookType>();
			SpcOutlookTimes? times = null;

			try
			{
				// Convective: one call yields every category; split + color each into its own cache file.
				var convective = await FetchIemAsync(day, date, cycle, "C", cancellationToken);
				if (convective is not null)
				{
					using var doc = JsonDocument.Parse(convective);
					foreach (var type in ConvectiveTypes)
					{
						if (SpcOutlookColors.TryBuildProduct(doc.RootElement, type, out var gj, out var t))
						{
							await WriteCacheAsync(PastCacheName(date, day, cycle, type), gj, cancellationToken);
							available.Add(type);
							times ??= t;
						}
					}
				}

				// Fire is a separate outlook_type.
				var fire = await FetchIemAsync(day, date, cycle, "F", cancellationToken);
				if (fire is not null)
				{
					using var doc = JsonDocument.Parse(fire);
					if (SpcOutlookColors.TryBuildProduct(doc.RootElement, SpcOutlookType.FireWeather, out var gj, out var t))
					{
						await WriteCacheAsync(PastCacheName(date, day, cycle, SpcOutlookType.FireWeather), gj, cancellationToken);
						available.Add(SpcOutlookType.FireWeather);
						times ??= t;
					}
				}
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				return new PastOutlookResult(false, cycle, Array.Empty<SpcOutlookType>(), null, ex.Message);
			}

			return new PastOutlookResult(available.Count > 0, cycle, available, times, null);
		}

		// Fetches one IEM outlook GeoJSON, or null on a non-success/empty response (so the caller can try
		// another cycle). Empty-but-valid collections parse fine and simply yield no products above.
		private async Task<string?> FetchIemAsync(int day, DateOnly date, int cycle, string outlookType, CancellationToken ct)
		{
			var url = $"{IemOutlookBase}?day={day}&valid={date:yyyy-MM-dd}&cycle={cycle}&outlook_type={outlookType}";
			using var response = await _http.GetAsync(url, ct);
			return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct) : null;
		}

		// Atomic write into the shared cache dir (temp + move), so a failed write never corrupts a cache file.
		private async Task WriteCacheAsync(string fileName, string content, CancellationToken ct)
		{
			var path = Path.Combine(CacheDirectory, fileName);
			var temp = path + ".tmp";
			await File.WriteAllTextAsync(temp, content, ct);
			File.Move(temp, path, overwrite: true);
		}

		private static string TypeSlug(SpcOutlookType type) => type switch
		{
			SpcOutlookType.Categorical => "cat",
			SpcOutlookType.Tornado => "torn",
			SpcOutlookType.Wind => "wind",
			SpcOutlookType.Hail => "hail",
			SpcOutlookType.ProbabilisticCombined => "prob",
			_ => "fire",
		};

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

		public async Task<string?> GetNarrativeAsync(SpcOutlookProduct product, CancellationToken cancellationToken = default)
		{
			var url = NarrativeUrlFor(product);
			if (url is null)
			{
				return null; // no supported narrative page (e.g. fire weather, for now)
			}

			var cacheFile = NarrativeCacheFileFor(product);
			try
			{
				var html = await _http.GetStringAsync(url, cancellationToken);
				var text = ExtractPreText(html);
				if (text is not null)
				{
					try { await File.WriteAllTextAsync(cacheFile, text, cancellationToken); }
					catch { /* cache write is best effort */ }
					return text;
				}
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch
			{
				// Fall through to the last-known-good cached copy below.
			}

			try
			{
				if (File.Exists(cacheFile))
				{
					return await File.ReadAllTextAsync(cacheFile, cancellationToken);
				}
			}
			catch { /* non-fatal */ }
			return null;
		}

		// Maps a product to its SPC forecast-discussion HTML page. One page per day group covers
		// all of that day's hazard sub-products (the Day-1 convective text discusses tornado,
		// wind, and hail together). Fire-weather pages use a different layout — deferred for now.
		private static string? NarrativeUrlFor(SpcOutlookProduct product)
		{
			if (product.Type is SpcOutlookType.FireWeather or SpcOutlookType.ExtendedFireWeather)
			{
				return null;
			}
			return product.Day switch
			{
				1 => "https://www.spc.noaa.gov/products/outlook/day1otlk.html",
				2 => "https://www.spc.noaa.gov/products/outlook/day2otlk.html",
				3 => "https://www.spc.noaa.gov/products/outlook/day3otlk.html",
				>= 4 and <= 8 => "https://www.spc.noaa.gov/products/exper/day4-8/",
				_ => null
			};
		}

		// One cached narrative per day group (shared by that day's hazard sub-products).
		private string NarrativeCacheFileFor(SpcOutlookProduct product)
		{
			var key = product.Day >= 4 ? "day4-8" : $"day{product.Day}";
			return Path.Combine(CacheDirectory, $"narrative-{key}.txt");
		}

		// Pulls the text out of the page's &lt;pre&gt; block (where SPC puts the product), strips
		// any inline tags (e.g. related-product &lt;a&gt; links), and decodes HTML entities.
		private static string? ExtractPreText(string html)
		{
			var open = html.IndexOf("<pre", StringComparison.OrdinalIgnoreCase);
			if (open < 0)
			{
				return null;
			}
			var contentStart = html.IndexOf('>', open);
			if (contentStart < 0)
			{
				return null;
			}
			contentStart++;
			var close = html.IndexOf("</pre>", contentStart, StringComparison.OrdinalIgnoreCase);
			if (close < 0)
			{
				return null;
			}

			var inner = html.Substring(contentStart, close - contentStart);
			inner = System.Text.RegularExpressions.Regex.Replace(inner, "<[^>]+>", string.Empty);
			inner = System.Net.WebUtility.HtmlDecode(inner);
			inner = inner.Trim('\r', '\n', ' ', '\t');
			return inner.Length > 0 ? inner : null;
		}
	}
}
