using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using OfflineMapsTest.Models;
using SharpCompress.Compressors;
using SharpCompress.Compressors.BZip2;

namespace OfflineMapsTest.Services
{
	/// <summary>
	/// Default <see cref="ILevel2RadarService"/>. Lists, downloads, and caches NEXRAD Level
	/// II volumes from the public AWS archive bucket <c>unidata-nexrad-level2</c>, extracting
	/// only the lowest-elevation reflectivity records (native bzip2, off the UI thread) so the
	/// WebView gets a small single-tilt buffer. Caches one file per volume timestamp so a loop
	/// only fetches volumes it doesn't already have. No WebView2 here — MainWindow maps the
	/// cache folder to the "radarlevel2" virtual host.
	/// </summary>
	public sealed class Level2RadarService : ILevel2RadarService
	{
		public const string CacheHostName = "radarlevel2";

		private const string BucketBase = "https://unidata-nexrad-level2.s3.amazonaws.com/";
		private static readonly XNamespace S3 = "http://s3.amazonaws.com/doc/2006-03-01/";

		private readonly HttpClient _http;

		public Level2RadarService()
		{
			CacheDirectory = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"OfflineMapsTest", "RadarLevel2");
			Directory.CreateDirectory(CacheDirectory);

			_http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
			_http.DefaultRequestHeaders.UserAgent.ParseAdd("OfflineMapsTest/1.0");
		}

		public string CacheDirectory { get; }

		public async Task<IReadOnlyList<string>> GetRecentKeysAsync(RadarSite site, int count, CancellationToken cancellationToken = default)
		{
			var now = DateTimeOffset.UtcNow;
			var keys = await KeysForDayAsync(site.Id, now, cancellationToken);

			// Early in the UTC day there may be fewer than `count` today; prepend yesterday.
			if (keys.Count < count)
			{
				var prev = await KeysForDayAsync(site.Id, now.AddDays(-1), cancellationToken);
				prev.AddRange(keys);
				keys = prev;
			}

			var recent = keys.Count > count ? keys.GetRange(keys.Count - count, count) : keys;
			PruneCache(site.Id, recent);
			return recent;
		}

		public async Task<RadarVolume?> EnsureCachedAsync(RadarSite site, string key, CancellationToken cancellationToken = default)
		{
			var time = ParseVolumeTime(key) ?? DateTimeOffset.UtcNow;
			var cacheFile = CacheFileFor(site.Id, time);
			var localUrl = LocalUrlFor(site.Id, time);

			// Reuse a volume we've already fetched + extracted.
			if (File.Exists(cacheFile))
			{
				return new RadarVolume(localUrl, site, time);
			}

			try
			{
				byte[] raw;
				using (var response = await _http.GetAsync(BucketBase + key, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
				{
					response.EnsureSuccessStatusCode();
					raw = await response.Content.ReadAsByteArrayAsync(cancellationToken);
				}

				// Extract only the lowest tilt on a worker thread; fall back to raw on failure.
				var toWrite = await Task.Run(() =>
				{
					try { return TryExtractLowestTilt(raw, site.Id) ?? raw; }
					catch (Exception ex)
					{
						System.Diagnostics.Debug.WriteLine($"[radar] {site.Id} extract failed, caching raw: {ex.Message}");
						return raw;
					}
				}, cancellationToken);

				var temp = cacheFile + ".tmp";
				await File.WriteAllBytesAsync(temp, toWrite, cancellationToken);
				File.Move(temp, cacheFile, overwrite: true);
				return new RadarVolume(localUrl, site, time);
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"[radar] {site.Id} fetch {key} failed: {ex.Message}");
				return null;
			}
		}

		private string CacheFileFor(string siteId, DateTimeOffset time) =>
			Path.Combine(CacheDirectory, $"{siteId}_{time:yyyyMMdd_HHmmss}.V06");

		private static string LocalUrlFor(string siteId, DateTimeOffset time) =>
			$"https://{CacheHostName}/{siteId}_{time:yyyyMMdd_HHmmss}.V06";

		// Deletes this site's cached volumes that aren't in the current keep-set (keyed by the
		// timestamp embedded in each key), so the loop cache doesn't grow without bound.
		private void PruneCache(string siteId, IReadOnlyList<string> keepKeys)
		{
			try
			{
				var keep = new HashSet<string>(
					keepKeys.Select(k => ParseVolumeTime(k))
						.Where(t => t is not null)
						.Select(t => CacheFileFor(siteId, t!.Value)),
					StringComparer.OrdinalIgnoreCase);

				foreach (var file in Directory.EnumerateFiles(CacheDirectory, $"{siteId}_*.V06"))
				{
					if (!keep.Contains(file))
					{
						try { File.Delete(file); } catch { /* best effort */ }
					}
				}
			}
			catch
			{
				// Pruning is an optimization; failing to prune is non-fatal.
			}
		}

		// Lists all _V06 keys (ascending) under a day prefix, paging through if needed.
		private async Task<List<string>> KeysForDayAsync(string siteId, DateTimeOffset day, CancellationToken ct)
		{
			var prefix = $"{day:yyyy/MM/dd}/{siteId}/";
			var keys = new List<string>();
			string? continuation = null;

			do
			{
				var url = $"{BucketBase}?list-type=2&prefix={Uri.EscapeDataString(prefix)}&max-keys=1000";
				if (continuation is not null)
				{
					url += $"&continuation-token={Uri.EscapeDataString(continuation)}";
				}

				var xml = await _http.GetStringAsync(url, ct);
				var doc = XDocument.Parse(xml);

				foreach (var keyEl in doc.Descendants(S3 + "Key"))
				{
					var k = keyEl.Value;
					// Volume files end in _V06; skip the small _MDM metadata sidecars.
					if (k.EndsWith("_V06", StringComparison.Ordinal))
					{
						keys.Add(k);
					}
				}

				continuation = doc.Root?.Element(S3 + "IsTruncated")?.Value == "true"
					? doc.Root?.Element(S3 + "NextContinuationToken")?.Value
					: null;
			}
			while (continuation is not null);

			return keys;
		}

		// Builds a minimal uncompressed volume containing ONLY the lowest elevation's records.
		// See the design notes in the previous revision: 24-byte header + decompressed LDM
		// records up to the first elevation-2 record (records align to elevations, lowest
		// first). Returns null if nothing parses (caller caches the raw bytes).
		private static byte[]? TryExtractLowestTilt(byte[] raw, string siteId)
		{
			const int headerSize = 24;
			if (raw.Length < headerSize + 4)
			{
				return null;
			}

			var icao = System.Text.Encoding.ASCII.GetBytes(siteId);
			using var output = new MemoryStream(8 * 1024 * 1024);
			output.Write(raw, 0, headerSize);

			var pos = headerSize;
			var records = 0;
			while (pos + 4 <= raw.Length)
			{
				var controlWord = (raw[pos] << 24) | (raw[pos + 1] << 16) | (raw[pos + 2] << 8) | raw[pos + 3];
				pos += 4;
				var size = Math.Abs(controlWord);
				if (size <= 0 || pos + size > raw.Length)
				{
					break;
				}

				byte[] block;
				using (var blockIn = new MemoryStream(raw, pos, size, writable: false))
				using (var bz = new BZip2Stream(blockIn, CompressionMode.Decompress, false))
				using (var blockOut = new MemoryStream(1024 * 1024))
				{
					bz.CopyTo(blockOut);
					block = blockOut.ToArray();
				}
				pos += size;

				if (ElevationOf(block, icao) >= 2)
				{
					break;
				}

				output.Write(block, 0, block.Length);
				records++;
			}

			return records > 0 ? output.ToArray() : null;
		}

		private static int ElevationOf(byte[] block, byte[] icao)
		{
			var i = IndexOf(block, icao);
			return (i >= 0 && i + 22 < block.Length) ? block[i + 22] : 0;
		}

		private static int IndexOf(byte[] haystack, byte[] needle)
		{
			var last = haystack.Length - needle.Length;
			for (var i = 0; i <= last; i++)
			{
				var k = 0;
				while (k < needle.Length && haystack[i + k] == needle[k])
				{
					k++;
				}
				if (k == needle.Length)
				{
					return i;
				}
			}
			return -1;
		}

		private static DateTimeOffset? ParseVolumeTime(string key)
		{
			var name = key.Substring(key.LastIndexOf('/') + 1); // e.g. KTLX20260613_214422_V06
			if (name.Length < 19)
			{
				return null;
			}

			var stamp = name.Substring(4, 15); // skip the 4-char ICAO -> "20260613_214422"
			return DateTimeOffset.TryParseExact(
				stamp, "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture,
				DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var vt)
				? vt
				: null;
		}
	}
}
