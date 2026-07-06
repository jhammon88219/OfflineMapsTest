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
using static OfflineMapsTest.Services.Level2Format;

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
		// Near-real-time chunk bucket: per-volume folders of S/I/E chunks streamed via LDM as
		// each completes (see GetLiveFrameAsync). Keys: <SITE>/<VOLUME#>/<yyyyMMdd>-<HHmmss>-<seq>-<S|I|E>.
		private const string ChunksBase = "https://unidata-nexrad-level2-chunks.s3.amazonaws.com/";
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

		public async Task<IReadOnlyList<string>> GetKeysForWindowAsync(RadarSite site, DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken = default)
		{
			if (endUtc < startUtc)
			{
				(startUtc, endUtc) = (endUtc, startUtc);
			}

			// List every UTC day the window touches (the bucket paths are <y>/<m>/<d>/<SITE>/), then
			// keep only the volumes whose scan time lands inside the window. Unlike the live path we do
			// NOT prune the cache here — past frames the user loaded shouldn't be deleted by a later
			// live loop's prune (and vice-versa); the cache simply accumulates.
			var all = new List<string>();
			for (var day = startUtc.UtcDateTime.Date; day <= endUtc.UtcDateTime.Date; day = day.AddDays(1))
			{
				cancellationToken.ThrowIfCancellationRequested();
				all.AddRange(await KeysForDayAsync(site.Id, new DateTimeOffset(day, TimeSpan.Zero), cancellationToken));
			}

			return all
				.Select(k => (key: k, time: ParseVolumeTime(k)))
				.Where(x => x.time is { } t && t >= startUtc && t <= endUtc)
				.OrderBy(x => x.time)
				.Select(x => x.key)
				.ToList();
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

				// Archived volumes (older dates) are gzip-wrapped (key ends ".gz"); the underlying
				// bytes are the same AR2V format the extractor expects, so gunzip first. Recent volumes
				// are stored raw, so this only runs for the historical Past Event Viewer fetches.
				var isGz = key.EndsWith(".gz", StringComparison.Ordinal);

				// Extract only the lowest tilt on a worker thread; fall back to raw on failure.
				var toWrite = await Task.Run(() =>
				{
					try
					{
						var data = isGz ? Gunzip(raw) : raw;
						return TryExtractLowestTilt(data, site.Id) ?? data;
					}
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

		// Safety cap on chunks decoded per live build. A plain volume is ~67 chunks, but a SAILS
		// precip VCP (extra 0.5° re-scans mid-volume) runs well past 90 — capping at 90 froze the
		// in-progress volume before its latest sweep's Doppler completed (the stuck partial-velocity
		// wedge). 160 covers a SAILS-heavy VCP 12/212/215 volume; decode stays cheap (incremental —
		// only NEW chunks are pulled/decompressed each poll).
		private const int LiveChunkCap = 160;

		// Per-volume decoded-chunk cache so repeated polls of the same in-progress volume only
		// download + decompress the NEW chunks (bounds bandwidth to ~one volume's worth). Keyed
		// by site/volume; reset when the newest volume changes.
		private string? _liveVolKey;
		private byte[]? _liveHeader;
		private readonly SortedDictionary<int, (byte[] block, int elev)> _liveBlocks = new();

		// One-shot cache of the previous-volume fallback frame (used while the newest volume is
		// still mid-scan). That volume is finished and immutable, so we build it once and reuse it
		// across the repeated polls instead of re-downloading ~a volume's worth of chunks each time.
		private string? _fallbackKey;
		private RadarVolume? _fallbackVolume;

		public async Task<RadarVolume?> GetLiveFrameAsync(RadarSite site, CancellationToken cancellationToken = default)
		{
			try
			{
				var newest = await FindNewestChunkVolumeAsync(site.Id, cancellationToken);
				if (newest is null)
				{
					RadarDiagnostics.Log("svc", "live", ("site", site.Id), ("msg", "no chunk volumes found"));
					return null;
				}

				var (vol, start) = newest.Value;
				RadarDiagnostics.Log("svc", "live", ("site", site.Id), ("vol", vol),
					("startZ", start.ToUniversalTime().ToString("HH:mm:ss")),
					("ageMin", Math.Round((DateTimeOffset.UtcNow - start).TotalMinutes, 1)));

				// Build from the newest volume, accumulating its chunks across polls (it's the
				// growing in-progress one).
				var frame = await BuildLiveFrameAsync(site, vol, start, useCache: true, cancellationToken);
				if (frame is not null)
				{
					return frame;
				}

				// The newest volume has no usable single tilt yet — it's still mid-scan and hasn't
				// finished its first 0.5° sweep. That can be a minute or two on slow clear-air VCPs
				// (a volume is ~10 min), during which we'd otherwise return nothing and the mode +
				// live frame stay "awaiting". Fall back to the PREVIOUS volume, which IS finished:
				// its sweep is a bit older but usually still fresher than (or equal to) the archive
				// newest, and it surfaces the scan mode right away. That volume is immutable, so we
				// build it once and reuse it for the repeated polls until the newest catches up.
				if (int.TryParse(vol, out var n) && n > 1)
				{
					var prevVol = (n - 1).ToString(CultureInfo.InvariantCulture);
					var prevStart = await NewestVolumeStartAsync(site.Id, n - 1, cancellationToken);
					if (prevStart is { } ps)
					{
						var fbKey = $"{site.Id}/{prevVol}/{ps:yyyyMMddHHmmss}";
						if (_fallbackKey == fbKey && _fallbackVolume is not null)
						{
							return _fallbackVolume;
						}
						RadarDiagnostics.Log("svc", "live", ("site", site.Id), ("vol", vol),
							("msg", $"newest not ready -> fall back to finished vol={prevVol}"));
						var fb = await BuildLiveFrameAsync(site, prevVol, ps, useCache: false, cancellationToken);
						if (fb is not null)
						{
							_fallbackKey = fbKey;
							_fallbackVolume = fb;
						}
						return fb;
					}
				}
				return null;
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"[radar] {site.Id} live frame failed: {ex.Message}");
				RadarDiagnostics.Log("svc", "live", ("site", site.Id), ("error", ex.Message));
				return null;
			}
		}

		// Builds a single-tilt live frame from one chunk volume's lowest 0.5° sweep. When useCache,
		// accumulates chunks across polls in the _liveVol* cache (for the growing newest volume);
		// otherwise it's a one-shot download into locals (for the previous-volume fallback). Returns
		// null if the volume has no S header chunk or no complete 0.5° sweep yet.
		private async Task<RadarVolume?> BuildLiveFrameAsync(RadarSite site, string vol, DateTimeOffset start, bool useCache, CancellationToken ct)
		{
			// The folder can hold chunks from several volumes (it's reused as the number cycles), so
			// keep only the chunks of the target volume (matching start stamp).
			var chunks = (await ListChunkObjectsAsync(site.Id, vol, ct))
				.Where(c => ParseChunkStart(c.key) == start)
				.ToList();
			if (chunks.Count == 0 || !chunks.Any(c => c.kind == 'S'))
			{
				RadarDiagnostics.Log("svc", "live", ("site", site.Id), ("vol", vol),
					("msg", $"no S chunk ({chunks.Count} chunks) -> can't decode"));
				return null; // no header chunk (older volume, partially expired) -> can't decode
			}
			chunks.Sort((a, b) => a.seq.CompareTo(b.seq));

			// The newest (in-progress) volume reuses the cross-poll cache; the fallback uses locals.
			SortedDictionary<int, (byte[] block, int elev)> blocks;
			byte[]? header;
			if (useCache)
			{
				// Switch volumes -> drop the cache and start accumulating the new one. Keyed by
				// start too, since the same folder number is reused across cycles.
				var cacheKey = $"{site.Id}/{vol}/{start:yyyyMMddHHmmss}";
				if (_liveVolKey != cacheKey)
				{
					_liveVolKey = cacheKey;
					_liveHeader = null;
					_liveBlocks.Clear();
				}
				blocks = _liveBlocks;
				header = _liveHeader;
			}
			else
			{
				blocks = new SortedDictionary<int, (byte[] block, int elev)>();
				header = null;
			}

			// Download + decompress only chunks we don't already have. Each chunk is one LDM
			// record: S = [24-byte header][control word][BZh…], I/E = [control word][BZh…].
			var icao = System.Text.Encoding.ASCII.GetBytes(site.Id);
			var added = 0;
			foreach (var c in chunks)
			{
				if (blocks.Count >= LiveChunkCap)
				{
					break;
				}
				if (blocks.ContainsKey(c.seq))
				{
					continue;
				}
				ct.ThrowIfCancellationRequested();

				byte[] bytes;
				using (var resp = await _http.GetAsync(ChunksBase + c.key, HttpCompletionOption.ResponseHeadersRead, ct))
				{
					if (!resp.IsSuccessStatusCode)
					{
						if (c.kind == 'S')
						{
							return null; // header vanished mid-read; bail
						}
						continue; // a later chunk expired; use what we have
					}
					bytes = await resp.Content.ReadAsByteArrayAsync(ct);
				}

				var isS = c.kind == 'S';
				if (isS && bytes.Length >= 24)
				{
					header = bytes[..24];
					if (useCache)
					{
						_liveHeader = header;
					}
				}

				var block = await Task.Run(() => DecompressChunk(bytes, isS), ct);
				if (block is null)
				{
					continue;
				}
				blocks[c.seq] = (block, ElevationOf(block, icao));
				added++;
			}

			if (header is null)
			{
				RadarDiagnostics.Log("svc", "live", ("site", site.Id), ("vol", vol),
					("msg", "header chunk missing -> can't decode"));
				return null;
			}

			var ordered = blocks.Values.ToList();
			var hdr = header;
			var sel = await Task.Run(() => SelectLatestSweep(hdr, ordered, icao), ct);
			if (!sel.complete || sel.data is null || !sel.velComplete)
			{
				RadarDiagnostics.Log("svc", "live", ("site", site.Id), ("vol", vol),
					("msg", $"no complete sweep yet (refl={sel.complete} vel={sel.velComplete}, {blocks.Count} chunks, +{added}) -> fall back"));
				return null;
			}

			// Trust the parsed radial time only if it's plausibly within this volume's life
			// (>= volume start, <= now); otherwise fall back to the volume-start timestamp so
			// a bad parse can never produce a bogus age.
			var nowUtc = DateTimeOffset.UtcNow;
			var ts = sel.dataTime is { } dt && dt >= start.AddMinutes(-2) && dt <= nowUtc.AddMinutes(2)
				? dt
				: start;
			var cacheFile = LiveCacheFileFor(site.Id, ts);
			if (!File.Exists(cacheFile))
			{
				var temp = cacheFile + ".tmp";
				await File.WriteAllBytesAsync(temp, sel.data, ct);
				File.Move(temp, cacheFile, overwrite: true);
			}
			PruneLiveCache(site.Id, cacheFile);

			var mode = DescribeMode(sel.vcp, sel.sweeps);
			RadarDiagnostics.Log("svc", "live", ("site", site.Id), ("vol", vol),
				("builtZ", ts.ToUniversalTime().ToString("HH:mm:ss")),
				("ageMin", Math.Round((DateTimeOffset.UtcNow - ts).TotalMinutes, 1)),
				("mode", mode),
				("msg", $"BUILT latest 0.5° sweep ({blocks.Count} chunks)"));
			return new RadarVolume(LiveUrlFor(site.Id, ts), site, ts, mode);
		}

		// Decompresses one chunk's single LDM record. S chunks carry the 24-byte volume header +
		// a 4-byte control word before the bzip2 stream; I/E chunks just the control word.
		private static byte[]? DecompressChunk(byte[] chunk, bool isS)
		{
			var offset = isS ? 28 : 4;
			if (chunk.Length <= offset)
			{
				return null;
			}
			try
			{
				using var input = new MemoryStream(chunk, offset, chunk.Length - offset, writable: false);
				using var bz = new BZip2Stream(input, CompressionMode.Decompress, false);
				using var output = new MemoryStream(1024 * 1024);
				bz.CopyTo(output);
				return output.ToArray();
			}
			catch
			{
				return null; // a partial/truncated trailing chunk of an in-progress volume
			}
		}


		// Finds the newest volume folder in the chunks bucket for a site, returning its number
		// and start time. Volume numbers cycle 1..999 and wrap, AND stale orphans span the whole
		// 1..999 range (each site sits at a different point in its cycle), so the number tells us
		// nothing reliable about recency — the newest can be anywhere (e.g. KVNX=383, KFDR=240).
		// A full key scan to read every timestamp costs ~40 list requests, so instead we exploit
		// structure: present numbers form a few contiguous RUNS separated by gaps, and the newest
		// volume always ends one run. We collect the ends of the substantial runs (plus the run
		// holding the lowest number, to catch a freshly-wrapped run starting at 1) and peek just
		// those few timestamps — typically 2-3 requests — taking the latest.
		private async Task<(string vol, DateTimeOffset start)?> FindNewestChunkVolumeAsync(string siteId, CancellationToken ct)
		{
			var folders = await ListChunkVolumeFoldersAsync(siteId, ct);
			var nums = folders
				.Select(f => int.TryParse(f, out var n) ? n : -1)
				.Where(n => n >= 0)
				.Distinct()
				.OrderBy(n => n)
				.ToList();
			if (nums.Count == 0)
			{
				return null;
			}

			// Split the present numbers into contiguous runs.
			var runs = new List<(int lo, int hi)>();
			var runStart = nums[0];
			for (var i = 1; i <= nums.Count; i++)
			{
				if (i == nums.Count || nums[i] != nums[i - 1] + 1)
				{
					runs.Add((runStart, nums[i - 1]));
					if (i < nums.Count)
					{
						runStart = nums[i];
					}
				}
			}

			// Peek a folder's NEWEST volume start (memoized within this call), so the binary
			// search doesn't re-list folders.
			var peeked = new Dictionary<int, DateTimeOffset?>();
			async Task<DateTimeOffset?> Ts(int num)
			{
				if (!peeked.TryGetValue(num, out var v))
				{
					v = await NewestVolumeStartAsync(siteId, num, ct);
					peeked[num] = v;
				}
				return v;
			}

			const int minRunForCandidate = 5;
			(string vol, DateTimeOffset start)? best = null;
			void Consider(int num, DateTimeOffset? ts)
			{
				if (ts is { } t && (best is null || t > best.Value.start))
				{
					best = (num.ToString(CultureInfo.InvariantCulture), t);
				}
			}

			foreach (var (lo, hi) in runs)
			{
				ct.ThrowIfCancellationRequested();
				if (hi - lo + 1 < minRunForCandidate && lo != nums[0])
				{
					continue; // skip tiny orphan runs (but keep the run holding the lowest number)
				}

				var tsLo = await Ts(lo);
				var tsHi = await Ts(hi);
				if (tsLo is null) { Consider(hi, tsHi); continue; }
				if (tsHi is null) { Consider(lo, tsLo); continue; }

				if (tsLo.Value <= tsHi.Value)
				{
					Consider(hi, tsHi); // monotonic in time: newest at the run's numeric end
				}
				else
				{
					// The run is contiguous in NUMBER but wraps in TIME — folders are reused, and
					// retention drops the oldest, so the current cycle's (newest) data sits at the
					// LOW end and the previous cycle's leftovers at the high end. The newest is the
					// peak: the largest number whose newest start is still >= the low end's. Binary
					// search (rotated-array max), ~log(n) peeks.
					var target = tsLo.Value;
					int l = lo, r = hi, ans = lo;
					var ansTs = tsLo.Value;
					while (l <= r)
					{
						ct.ThrowIfCancellationRequested();
						var mid = l + (r - l) / 2;
						var tm = await Ts(mid);
						if (tm is { } t && t >= target) { ans = mid; ansTs = t; l = mid + 1; }
						else { r = mid - 1; }
					}
					Consider(ans, ansTs);
				}
			}
			return best;
		}

		// Lists the volume "folders" (CommonPrefixes) under <siteId>/ in the chunks bucket.
		private async Task<List<string>> ListChunkVolumeFoldersAsync(string siteId, CancellationToken ct)
		{
			var prefix = $"{siteId}/";
			var result = new List<string>();
			string? continuation = null;

			do
			{
				var url = $"{ChunksBase}?list-type=2&delimiter=%2F&prefix={Uri.EscapeDataString(prefix)}&max-keys=1000";
				if (continuation is not null)
				{
					url += $"&continuation-token={Uri.EscapeDataString(continuation)}";
				}

				var xml = await _http.GetStringAsync(url, ct);
				var doc = XDocument.Parse(xml);
				foreach (var cp in doc.Descendants(S3 + "CommonPrefixes"))
				{
					var p = cp.Element(S3 + "Prefix")?.Value; // e.g. "KTLX/933/"
					var parts = p?.TrimEnd('/').Split('/');
					if (parts is { Length: 2 })
					{
						result.Add(parts[1]);
					}
				}

				continuation = doc.Root?.Element(S3 + "IsTruncated")?.Value == "true"
					? doc.Root?.Element(S3 + "NextContinuationToken")?.Value
					: null;
			}
			while (continuation is not null);

			return result;
		}

		// The NEWEST volume start time in a folder. A folder is reused as the volume number cycles,
		// so it can hold chunks from several volumes (each with its own start stamp); we want the
		// most recent, which is the max chunk timestamp (NOT the lexically-first/oldest key).
		private async Task<DateTimeOffset?> NewestVolumeStartAsync(string siteId, int vol, CancellationToken ct)
		{
			var url = $"{ChunksBase}?list-type=2&max-keys=1000&prefix={Uri.EscapeDataString($"{siteId}/{vol}/")}";
			var xml = await _http.GetStringAsync(url, ct);
			DateTimeOffset? max = null;
			foreach (var keyEl in XDocument.Parse(xml).Descendants(S3 + "Key"))
			{
				if (ParseChunkStart(keyEl.Value) is { } t && (max is null || t > max))
				{
					max = t;
				}
			}
			return max;
		}

		// Lists all chunks in a volume folder as (key, seq, kind), e.g. (..., 2, 'I').
		private async Task<List<(string key, int seq, char kind)>> ListChunkObjectsAsync(string siteId, string vol, CancellationToken ct)
		{
			var prefix = $"{siteId}/{vol}/";
			var list = new List<(string, int, char)>();
			string? continuation = null;

			do
			{
				var url = $"{ChunksBase}?list-type=2&prefix={Uri.EscapeDataString(prefix)}&max-keys=1000";
				if (continuation is not null)
				{
					url += $"&continuation-token={Uri.EscapeDataString(continuation)}";
				}

				var xml = await _http.GetStringAsync(url, ct);
				var doc = XDocument.Parse(xml);
				foreach (var keyEl in doc.Descendants(S3 + "Key"))
				{
					if (ParseChunkSeqKind(keyEl.Value) is { } pk)
					{
						list.Add((keyEl.Value, pk.seq, pk.kind));
					}
				}

				continuation = doc.Root?.Element(S3 + "IsTruncated")?.Value == "true"
					? doc.Root?.Element(S3 + "NextContinuationToken")?.Value
					: null;
			}
			while (continuation is not null);

			return list;
		}

		// Chunk key tail is "<yyyyMMdd>-<HHmmss>-<seq>-<S|I|E>", e.g. "20260614-235853-001-S".
		private static DateTimeOffset? ParseChunkStart(string key)
		{
			var name = key.Substring(key.LastIndexOf('/') + 1);
			if (name.Length < 15)
			{
				return null;
			}

			return DateTimeOffset.TryParseExact(
				name.Substring(0, 15), "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture,
				DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t)
				? t
				: null;
		}

		private static (int seq, char kind)? ParseChunkSeqKind(string key)
		{
			var name = key.Substring(key.LastIndexOf('/') + 1);
			var parts = name.Split('-');
			if (parts.Length < 4 || !int.TryParse(parts[2], out var seq) || parts[3].Length == 0)
			{
				return null;
			}

			return (seq, parts[3][0]);
		}

		private string CacheFileFor(string siteId, DateTimeOffset time) =>
			Path.Combine(CacheDirectory, $"{siteId}_{time:yyyyMMdd_HHmmss}.V06");

		private static string LocalUrlFor(string siteId, DateTimeOffset time) =>
			$"https://{CacheHostName}/{siteId}_{time:yyyyMMdd_HHmmss}.V06";

		// Live (chunks) frames get a "_live_" infix so they're served from the same host but
		// never confused with archive loop frames (and skipped by the archive prune below).
		private string LiveCacheFileFor(string siteId, DateTimeOffset time) =>
			Path.Combine(CacheDirectory, $"{siteId}_live_{time:yyyyMMdd_HHmmss}.V06");

		private static string LiveUrlFor(string siteId, DateTimeOffset time) =>
			$"https://{CacheHostName}/{siteId}_live_{time:yyyyMMdd_HHmmss}.V06";

		// Deletes this site's cached volumes that aren't in the current keep-set (keyed by the
		// timestamp embedded in each key), so the loop cache doesn't grow without bound. Live
		// frames are pruned separately (PruneLiveCache) and excluded here.
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
					if (file.Contains("_live_", StringComparison.OrdinalIgnoreCase))
					{
						continue; // owned by PruneLiveCache
					}
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

		// Keeps only the newest live frame for a site (best effort).
		private void PruneLiveCache(string siteId, string keepFile)
		{
			try
			{
				foreach (var file in Directory.EnumerateFiles(CacheDirectory, $"{siteId}_live_*.V06"))
				{
					if (!string.Equals(file, keepFile, StringComparison.OrdinalIgnoreCase))
					{
						try { File.Delete(file); } catch { /* best effort */ }
					}
				}
			}
			catch
			{
				// Non-fatal.
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
					if (IsVolumeKey(k))
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

		// A site whose newest archive volume is older than this is treated as "down" (offline).
		// This is measured against the ARCHIVE bucket, which itself lags real time by ~10 min (the
		// reason the chunks bucket exists for the live frame). Stacked on a clear-air VCP's ~10-min
		// volume cadence, a perfectly healthy quiet site's newest archive volume is routinely
		// ~20-25 min old — so the threshold must clear that or clear-air sites false-flag as down
		// (the KMQT/KIWA/KSGF/KINX-etc. false positives). 30 min gives headroom over the worst
		// healthy case while still catching a genuine outage (which keeps climbing past it).
		private static readonly TimeSpan LiveSiteStaleness = TimeSpan.FromMinutes(30);

		public async Task<IReadOnlyCollection<string>> GetLiveSiteIdsAsync(CancellationToken cancellationToken = default)
		{
			var now = DateTimeOffset.UtcNow;

			// A site counts as "live" only if its NEWEST volume is recent (within LiveSiteStaleness).
			// The old existence-only check (any data today OR yesterday) couldn't catch a site that
			// produced data earlier in the UTC day and then went down — its folder still exists, so it
			// stayed green for up to ~2 days (the KTLX-shows-green bug). So we (1) discover the candidate
			// sites cheaply via the delimited day listing, then (2) probe each candidate's newest volume
			// time and keep only the fresh ones.
			var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			// today + yesterday so a site isn't missed in the first minutes of the UTC day.
			foreach (var day in new[] { now, now.AddDays(-1) })
			{
				try
				{
					await AddSitesForDayAsync(day, candidates, cancellationToken);
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch
				{
					// Best effort: a failed listing just leaves those sites out of the candidate set.
				}
			}

			// Probe each candidate's newest volume time with bounded concurrency; a stale newest means
			// the site stopped transmitting = "down". (Sites absent from the candidate set are down
			// already and need no probe.)
			var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			using var gate = new SemaphoreSlim(10);
			var probes = candidates.Select(async id =>
			{
				await gate.WaitAsync(cancellationToken);
				bool fresh;
				try
				{
					var newest = await NewestArchiveVolumeTimeAsync(id, now, cancellationToken);
					fresh = newest is { } t && now - t <= LiveSiteStaleness;
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch
				{
					// Best effort: a transient probe failure errs toward "available" so a network blip
					// doesn't falsely flag a healthy site as down.
					fresh = true;
				}
				finally
				{
					gate.Release();
				}

				if (fresh)
				{
					lock (live)
					{
						live.Add(id);
					}
				}
			});
			await Task.WhenAll(probes);
			return live;
		}

		// Newest archive volume time for a site: the last (chronologically) volume key today, or —
		// only when today has nothing yet (just after UTC midnight, or a site down since before it) —
		// yesterday's newest. Volume keys sort lexically == chronologically (the timestamp is embedded
		// in the name), so the max parsed time wins. One listing per site in the common case.
		private async Task<DateTimeOffset?> NewestArchiveVolumeTimeAsync(string siteId, DateTimeOffset now, CancellationToken ct)
		{
			var newest = NewestOf(await KeysForDayAsync(siteId, now, ct));
			if (newest is null)
			{
				newest = NewestOf(await KeysForDayAsync(siteId, now.AddDays(-1), ct));
			}
			return newest;

			static DateTimeOffset? NewestOf(List<string> keys)
			{
				DateTimeOffset? max = null;
				foreach (var k in keys)
				{
					var t = ParseVolumeTime(k);
					if (t is { } v && (max is null || v > max))
					{
						max = v;
					}
				}
				return max;
			}
		}

		public async Task<IReadOnlyCollection<string>> GetSiteIdsForDateAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken = default)
		{
			var sites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			// The window spans at most a couple of UTC days; list each day's site folders.
			for (var day = startUtc.UtcDateTime.Date; day <= endUtc.UtcDateTime.Date; day = day.AddDays(1))
			{
				try
				{
					await AddSitesForDayAsync(new DateTimeOffset(day, TimeSpan.Zero), sites, cancellationToken);
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch
				{
					// Best effort: a failed listing just leaves those sites unflagged (shown available).
				}
			}
			return sites;
		}

		// Adds every site that has any data under <y>/<m>/<d>/ to the set, via a single
		// delimited listing whose CommonPrefixes are <y>/<m>/<d>/<SITE>/ (one request/day).
		private async Task AddSitesForDayAsync(DateTimeOffset day, HashSet<string> into, CancellationToken ct)
		{
			var prefix = $"{day:yyyy/MM/dd}/";
			string? continuation = null;

			do
			{
				var url = $"{BucketBase}?list-type=2&delimiter=%2F&prefix={Uri.EscapeDataString(prefix)}&max-keys=1000";
				if (continuation is not null)
				{
					url += $"&continuation-token={Uri.EscapeDataString(continuation)}";
				}

				var xml = await _http.GetStringAsync(url, ct);
				var doc = XDocument.Parse(xml);
				foreach (var cp in doc.Descendants(S3 + "CommonPrefixes"))
				{
					var parts = cp.Element(S3 + "Prefix")?.Value?.TrimEnd('/').Split('/'); // "2026/06/15/KLIX/"
					if (parts is { Length: 4 })
					{
						into.Add(parts[3]);
					}
				}

				continuation = doc.Root?.Element(S3 + "IsTruncated")?.Value == "true"
					? doc.Root?.Element(S3 + "NextContinuationToken")?.Value
					: null;
			}
			while (continuation is not null);
		}


		// True for a Level II VOLUME file. Two on-disk eras both qualify:
		//   • Modern super-res (2008+): the key (after an optional ".gz") ends with the archive-format
		//     version suffix "_V<NN>" — "_V06" (super-res + dual-pol, 2013+, all moments incl. CC) and
		//     "_V03".."_V05" (~2008-2012: refl/vel/spectrum-width, NO dual-pol → empty CC). Message 31.
		//   • Legacy (pre-~2008): NO version suffix, "<ICAO><yyyyMMdd>_<HHmmss>" (AR2V0001 / Message 1).
		//     gzip-wrapped → fully-uncompressed AR2V (no per-record bzip2). The vendored decoder's
		//     Message-1 path reads these; C# can't single-tilt extract them (no Message-31 moment
		//     markers / no LDM bzip2 records), so EnsureCachedAsync's fallback caches the whole
		//     gunzipped volume and the WebView decodes the lowest tilt.
		// It deliberately EXCLUDES the "_MDM" metadata sidecars in both eras.
		private static bool IsVolumeKey(string key)
		{
			var name = key.EndsWith(".gz", StringComparison.Ordinal) ? key[..^3] : key;
			name = name.Substring(name.LastIndexOf('/') + 1);
			if (name.Contains("_MDM", StringComparison.Ordinal))
			{
				return false; // metadata sidecar, not a volume
			}

			// Modern: "..._V06" (also _V03.._V05).
			if (name.Length >= 4
				&& name[^4] == '_' && name[^3] == 'V'
				&& char.IsDigit(name[^2]) && char.IsDigit(name[^1]))
			{
				return true;
			}

			// Legacy: "<ICAO><yyyyMMdd>_<HHmmss>" — 4-char ICAO, 8-digit date, '_', 6-digit time.
			return name.Length == 19
				&& char.IsLetter(name[0])
				&& name[12] == '_'
				&& AllDigits(name, 4, 8)
				&& AllDigits(name, 13, 6);
		}

		private static bool AllDigits(string s, int start, int count)
		{
			for (var i = start; i < start + count; i++)
			{
				if (!char.IsDigit(s[i]))
				{
					return false;
				}
			}
			return true;
		}

		// Decompresses a gzip-wrapped archive volume (older dates are stored as ..._V0x.gz). The
		// inner bytes are the same AR2V format TryExtractLowestTilt reads.
		private static byte[] Gunzip(byte[] gz)
		{
			using var input = new MemoryStream(gz);
			using var gzip = new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress);
			using var output = new MemoryStream(gz.Length * 4);
			gzip.CopyTo(output);
			return output.ToArray();
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
