using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// Default <see cref="IStormReportService"/>. Downloads SPC's filtered storm-report CSVs, transforms
	/// them into a single GeoJSON point collection per convective day, and caches it on disk. No WebView2
	/// here — MainWindow maps the cache folder to the "stormreports" virtual host so the page can fetch
	/// https://stormreports/reports-YYYYMMDD.geojson.
	/// </summary>
	public sealed class StormReportService : IStormReportService
	{
		/// <summary>WebView virtual host the cached files are served under. MainWindow owns the actual
		/// mapping; this constant is the shared contract.</summary>
		public const string CacheHostName = "stormreports";

		// SPC publishes per-type "filtered" (deduped / quality-controlled) reports as CSV, keyed by the
		// convective-day START date (yymmdd). These are the exact reports SPC uses to verify its outlooks.
		private const string ReportsBase = "https://www.spc.noaa.gov/climo/reports";

		// The SPC CSV truncates each report's remark to ~160 chars; the Iowa Environmental Mesonet LSR feed
		// carries the FULL narrative. We fetch the day's national LSRs once and match each SPC dot to its LSR
		// (same type, nearest time+location) to swap in the untruncated remark. Best-effort — a failed fetch
		// or an unmatched dot just keeps the SPC snippet.
		private const string IemLsrBase = "https://mesonet.agron.iastate.edu/geojson/lsr.geojson";

		// The three report types → the CSV kind slug and the property tag written into each feature.
		private static readonly (string Slug, string Kind)[] Kinds =
		{
			("torn", "torn"),
			("wind", "wind"),
			("hail", "hail"),
		};

		private readonly HttpClient _http;

		public StormReportService()
		{
			CacheDirectory = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"Anvil", "StormReports");
			Directory.CreateDirectory(CacheDirectory);

			_http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
			// SPC rejects requests without a User-Agent.
			_http.DefaultRequestHeaders.UserAgent.ParseAdd("Anvil/1.0");
		}

		public string CacheDirectory { get; }

		// Cache schema version — bumped when the built GeoJSON's shape/content changes so older cached files
		// (which the immutable path would otherwise reuse forever) are ignored and rebuilt. v2 = added the
		// full IEM LSR remark enrichment.
		private const string CacheVersion = "v2";

		/// <summary>Stable cache filename for one convective day's reports.</summary>
		public static string CacheName(DateOnly day) => $"reports-{CacheVersion}-{day:yyyyMMdd}.geojson";

		public string LocalUrl(DateOnly convectiveDay) => $"https://{CacheHostName}/{CacheName(convectiveDay)}";

		public async Task<StormReportResult> EnsureReportsAsync(DateOnly convectiveDay, bool immutable, CancellationToken cancellationToken = default)
		{
			var cacheFile = Path.Combine(CacheDirectory, CacheName(convectiveDay));

			// Historical days never change — reuse the cache and just report its counts.
			if (immutable && File.Exists(cacheFile))
			{
				var (t, w, h) = CountKinds(cacheFile);
				return new StormReportResult(true, t, w, h, null);
			}

			var reports = new List<ReportPoint>();
			var anyOk = false;
			try
			{
				foreach (var (slug, kind) in Kinds)
				{
					cancellationToken.ThrowIfCancellationRequested();
					var csv = await FetchCsvAsync(convectiveDay, slug, cancellationToken);
					if (csv is null) { continue; } // this type's file was missing — others may still exist
					anyOk = true;
					ParseInto(csv, kind, reports);
				}
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				// Fall through to the last-known-good handling below.
				return AnyOkOrCache(anyOk, reports, cacheFile, convectiveDay, ex.Message);
			}

			if (!anyOk)
			{
				return AnyOkOrCache(false, reports, cacheFile, convectiveDay, "SPC storm reports unavailable.");
			}

			// Swap SPC's truncated remarks for the full IEM LSR narratives where we can match them.
			await EnrichWithIemAsync(convectiveDay, reports, cancellationToken);

			int torn = 0, wind = 0, hail = 0;
			foreach (var r in reports)
			{
				if (r.Kind == "torn") { torn++; }
				else if (r.Kind == "wind") { wind++; }
				else { hail++; }
			}

			await WriteGeoJsonAsync(cacheFile, reports, cancellationToken);
			return new StormReportResult(true, torn, wind, hail, null);
		}

		// When a fetch failed: keep the last-known-good file (returning its counts) rather than blanking the
		// overlay; only surface an error if there's nothing cached to fall back on.
		private static StormReportResult AnyOkOrCache(bool anyOk, List<ReportPoint> reports, string cacheFile, DateOnly day, string error)
		{
			if (!anyOk && File.Exists(cacheFile))
			{
				var (t, w, h) = CountKinds(cacheFile);
				return new StormReportResult(true, t, w, h, null);
			}
			return new StormReportResult(false, 0, 0, 0, error);
		}

		// Fetches one report CSV, preferring the "filtered" (deduped / quality-controlled) file SPC verifies
		// against, but falling back to the raw file when filtered is missing — SPC only began publishing the
		// filtered files ~2012, so older archive dates (still a valid replay target) have only the raw ones.
		// Both share the same 8-column layout (the magnitude header name varies but we skip by "Time"), so
		// either parses identically. Returns null only when BOTH are non-success (a normal 404 for a type
		// with no reports, or a date SPC doesn't archive).
		private async Task<string?> FetchCsvAsync(DateOnly day, string slug, CancellationToken ct)
		{
			return await TryGetAsync($"{ReportsBase}/{day:yyMMdd}_rpts_filtered_{slug}.csv", ct)
				?? await TryGetAsync($"{ReportsBase}/{day:yyMMdd}_rpts_{slug}.csv", ct);
		}

		private async Task<string?> TryGetAsync(string url, CancellationToken ct)
		{
			using var response = await _http.GetAsync(url, ct);
			return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct) : null;
		}

		// ── Full-remark enrichment from the IEM LSR feed ──

		// The SPC report kind → the IEM LSR `type` code(s) it can match. Wind covers both measured gust (G)
		// and damage (D); tornado and hail are single codes.
		private static string[] LsrTypesFor(string kind) => kind switch
		{
			"torn" => new[] { "T" },
			"hail" => new[] { "H" },
			_ => new[] { "G", "D" }, // wind
		};

		// Fetches the convective day's national LSRs once and replaces each SPC report's (truncated) comment
		// with the matched LSR's full remark. Best-effort: any failure leaves the SPC snippets intact.
		private async Task EnrichWithIemAsync(DateOnly day, List<ReportPoint> reports, CancellationToken ct)
		{
			if (reports.Count == 0) { return; }

			// The convective day is 12Z→12Z; that's also the LSR window to pull.
			var startUtc = new DateTime(day.Year, day.Month, day.Day, 12, 0, 0, DateTimeKind.Utc);
			var endUtc = startUtc.AddDays(1);

			List<Lsr> lsrs;
			try
			{
				lsrs = await FetchIemLsrsAsync(startUtc, endUtc, ct);
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"[SPC] IEM LSR enrichment skipped: {ex.Message}");
				return;
			}
			if (lsrs.Count == 0) { return; }

			for (var i = 0; i < reports.Count; i++)
			{
				var r = reports[i];
				var at = AbsTime(day, r.Time);
				if (at is null) { continue; }

				var remark = BestRemark(r.Kind, r.Lat, r.Lon, at.Value, lsrs);
				if (!string.IsNullOrWhiteSpace(remark))
				{
					reports[i] = r with { Comments = remark.Trim() };
				}
			}
		}

		// Finds the fullest matching LSR remark for a report: same type group, within tolerance in BOTH time
		// (≤20 min) and location (≤0.3°), minimizing a combined time+distance score (the true match sits at
		// ~0 of both). Returns null when nothing matches, so the SPC snippet is kept.
		private static string? BestRemark(string kind, double lat, double lon, DateTime atUtc, List<Lsr> lsrs)
		{
			var types = LsrTypesFor(kind);
			string? best = null;
			var bestScore = double.MaxValue;
			foreach (var l in lsrs)
			{
				if (Array.IndexOf(types, l.Type) < 0) { continue; }
				var dtMin = Math.Abs((l.ValidUtc - atUtc).TotalMinutes);
				if (dtMin > 20) { continue; }
				var dLat = Math.Abs(l.Lat - lat);
				var dLon = Math.Abs(l.Lon - lon);
				if (dLat > 0.3 || dLon > 0.3) { continue; }

				var distKm = Math.Sqrt(dLat * dLat + dLon * dLon) * 111.0;
				var score = dtMin + distKm * 0.25;
				if (score < bestScore)
				{
					bestScore = score;
					best = l.Remark;
				}
			}
			return best;
		}

		// SPC report time is UTC HHMM over the 12Z→12Z convective day: hours ≥12 fall on the START date,
		// hours <12 on the next day. Returns the absolute UTC instant, or null if the field isn't HHMM.
		private static DateTime? AbsTime(DateOnly day, string hhmm)
		{
			var t = (hhmm ?? string.Empty).Trim();
			if (t.Length is < 3 or > 4 || !int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out _))
			{
				return null;
			}
			t = t.PadLeft(4, '0');
			if (!int.TryParse(t.AsSpan(0, 2), out var hh) || !int.TryParse(t.AsSpan(2, 2), out var mm) ||
				hh > 23 || mm > 59)
			{
				return null;
			}
			var d = hh >= 12 ? day : day.AddDays(1);
			return new DateTime(d.Year, d.Month, d.Day, hh, mm, 0, DateTimeKind.Utc);
		}

		// Parses the IEM LSR GeoJSON into the subset we match on. Keeps only the types we can match (T/H/G/D)
		// with a non-empty remark, so the match loop stays small.
		private async Task<List<Lsr>> FetchIemLsrsAsync(DateTime startUtc, DateTime endUtc, CancellationToken ct)
		{
			var url = $"{IemLsrBase}?sts={startUtc:yyyy-MM-ddTHH:mm}Z&ets={endUtc:yyyy-MM-ddTHH:mm}Z";
			var list = new List<Lsr>();

			using var response = await _http.GetAsync(url, ct);
			if (!response.IsSuccessStatusCode) { return list; }

			await using var stream = await response.Content.ReadAsStreamAsync(ct);
			using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
			if (!doc.RootElement.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array)
			{
				return list;
			}

			foreach (var f in features.EnumerateArray())
			{
				if (!f.TryGetProperty("properties", out var p)) { continue; }
				var type = GetString(p, "type");
				if (type is not ("T" or "H" or "G" or "D")) { continue; }
				var remark = GetString(p, "remark");
				if (string.IsNullOrWhiteSpace(remark)) { continue; }

				if (!TryGetCoords(f, p, out var lat, out var lon)) { continue; }
				var valid = GetString(p, "valid");
				if (valid is null || !DateTimeOffset.TryParse(valid, CultureInfo.InvariantCulture,
						DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var vt))
				{
					continue;
				}

				list.Add(new Lsr(type, lat, lon, vt.UtcDateTime, remark));
			}
			return list;
		}

		private static string? GetString(JsonElement obj, string name) =>
			obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

		// Coordinates from the geometry (preferred, [lon,lat]) or the properties lat/lon fallback.
		private static bool TryGetCoords(JsonElement feature, JsonElement props, out double lat, out double lon)
		{
			lat = lon = 0;
			if (feature.TryGetProperty("geometry", out var geom) &&
				geom.TryGetProperty("coordinates", out var coords) &&
				coords.ValueKind == JsonValueKind.Array && coords.GetArrayLength() >= 2 &&
				coords[0].TryGetDouble(out lon) && coords[1].TryGetDouble(out lat))
			{
				return true;
			}
			return props.TryGetProperty("lat", out var la) && la.TryGetDouble(out lat) &&
				props.TryGetProperty("lon", out var lo) && lo.TryGetDouble(out lon);
		}

		private readonly record struct Lsr(string Type, double Lat, double Lon, DateTime ValidUtc, string Remark);

		// Parses an SPC filtered-report CSV into points. Layout (torn/wind/hail share it, differing only in
		// the 2nd "magnitude" column name): Time, Mag, Location, County, State, Lat, Lon, Comments. The
		// header row (first field "Time") and any row with an unparseable lat/lon are skipped. Comment fields
		// are quoted when they contain commas, so a quote-aware split is required.
		private static void ParseInto(string csv, string kind, List<ReportPoint> into)
		{
			using var reader = new StringReader(csv);
			string? line;
			while ((line = reader.ReadLine()) is not null)
			{
				if (line.Length == 0) { continue; }
				var fields = SplitCsv(line);
				if (fields.Count < 7) { continue; }
				if (string.Equals(fields[0], "Time", StringComparison.OrdinalIgnoreCase)) { continue; } // header

				if (!double.TryParse(fields[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) ||
					!double.TryParse(fields[6], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
				{
					continue;
				}

				into.Add(new ReportPoint(
					Kind: kind,
					Lat: lat,
					Lon: lon,
					Time: fields[0],
					Mag: fields[1],
					Location: fields[2],
					County: fields[3],
					State: fields[4],
					Comments: fields.Count > 7 ? fields[7] : string.Empty));
			}
		}

		// Minimal RFC-4180-ish CSV split for a single line: comma-separated, fields may be double-quoted
		// (which lets them contain commas), and "" inside a quoted field is a literal quote. Enough for SPC's
		// files, which quote only the Comments column.
		private static List<string> SplitCsv(string line)
		{
			var fields = new List<string>(8);
			var sb = new StringBuilder();
			var inQuotes = false;
			for (var i = 0; i < line.Length; i++)
			{
				var c = line[i];
				if (inQuotes)
				{
					if (c == '"')
					{
						if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } // escaped quote
						else { inQuotes = false; }
					}
					else { sb.Append(c); }
				}
				else if (c == '"') { inQuotes = true; }
				else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
				else { sb.Append(c); }
			}
			fields.Add(sb.ToString());
			return fields;
		}

		// Writes the point collection atomically (temp + move). Uses Utf8JsonWriter so comment text with
		// quotes/newlines is escaped correctly.
		private static async Task WriteGeoJsonAsync(string cacheFile, List<ReportPoint> reports, CancellationToken ct)
		{
			var temp = cacheFile + ".tmp";
			await using (var stream = File.Create(temp))
			{
				await using var writer = new Utf8JsonWriter(stream);
				writer.WriteStartObject();
				writer.WriteString("type", "FeatureCollection");
				writer.WriteStartArray("features");
				foreach (var r in reports)
				{
					writer.WriteStartObject();
					writer.WriteString("type", "Feature");

					writer.WriteStartObject("geometry");
					writer.WriteString("type", "Point");
					writer.WriteStartArray("coordinates");
					writer.WriteNumberValue(r.Lon);
					writer.WriteNumberValue(r.Lat);
					writer.WriteEndArray();
					writer.WriteEndObject();

					writer.WriteStartObject("properties");
					writer.WriteString("kind", r.Kind);
					writer.WriteString("time", r.Time);
					writer.WriteString("mag", r.Mag);
					writer.WriteString("loc", r.Location);
					writer.WriteString("county", r.County);
					writer.WriteString("st", r.State);
					writer.WriteString("com", r.Comments);
					writer.WriteEndObject();

					writer.WriteEndObject();
				}
				writer.WriteEndArray();
				writer.WriteEndObject();
				await writer.FlushAsync(ct);
			}
			File.Move(temp, cacheFile, overwrite: true);
		}

		// Counts features by kind in a cached file (used for the immutable-reuse and fetch-failure paths).
		private static (int Torn, int Wind, int Hail) CountKinds(string cacheFile)
		{
			try
			{
				using var doc = JsonDocument.Parse(File.ReadAllText(cacheFile));
				if (!doc.RootElement.TryGetProperty("features", out var features) ||
					features.ValueKind != JsonValueKind.Array)
				{
					return (0, 0, 0);
				}
				int t = 0, w = 0, h = 0;
				foreach (var f in features.EnumerateArray())
				{
					if (!f.TryGetProperty("properties", out var props) ||
						!props.TryGetProperty("kind", out var kind)) { continue; }
					switch (kind.GetString())
					{
						case "torn": t++; break;
						case "wind": w++; break;
						case "hail": h++; break;
					}
				}
				return (t, w, h);
			}
			catch
			{
				return (0, 0, 0);
			}
		}

		private readonly record struct ReportPoint(
			string Kind, double Lat, double Lon, string Time, string Mag,
			string Location, string County, string State, string Comments);
	}
}
