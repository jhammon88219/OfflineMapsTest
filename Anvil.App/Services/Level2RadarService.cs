using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Anvil.Models;
using SharpCompress.Compressors;
using SharpCompress.Compressors.BZip2;
using static Anvil.Services.Level2Format;

namespace Anvil.Services
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
				"Anvil", "RadarLevel2");
			Directory.CreateDirectory(CacheDirectory);

			_http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
			_http.DefaultRequestHeaders.UserAgent.ParseAdd("Anvil/1.0");
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

		public async Task<RadarScanInfo?> GetLatestScanAsync(RadarSite site, CancellationToken cancellationToken = default)
		{
			// Newest ARCHIVE volume for the site (listing only), then ensure its lowest tilt is cached —
			// EnsureCachedAsync fills VolumeTime + ModeText (the VCP, parsed from the tilt's metadata).
			// Use the window listing (last few hours) rather than GetRecentKeysAsync so we DON'T prune the
			// site's cache: browsing a detail must never disturb a loop running on the same site.
			var now = DateTimeOffset.UtcNow;
			var keys = await GetKeysForWindowAsync(site, now.AddHours(-6), now, cancellationToken);
			if (keys.Count == 0)
			{
				return null; // no recent data in the feed for this site
			}

			var archive = await EnsureCachedAsync(site, keys[^1], tiltAngle: null, cancellationToken);
			if (archive is null)
			{
				return null;
			}

			// The archive runs ~5-10 min behind, but the loop's LIVE frame comes from the near-real-time
			// chunks bucket (~1-2 min) — so reporting the archive alone made this read minutes staler than
			// the loop showing the same site. Take the newest chunks volume's start when it's fresher. This
			// is a LISTING only (no chunk downloads, no _liveVolKey/_liveBlocks mutation), so it can't
			// disturb the live-frame cache of a loop polling a different site.
			var scanTime = archive.VolumeTime;
			try
			{
				if (await FindNewestChunkVolumeAsync(site.Id, cancellationToken) is { } newest
					&& newest.start > scanTime && newest.start <= now)
				{
					scanTime = newest.start;
				}
			}
			catch (OperationCanceledException) { throw; }
			catch { /* chunks are best-effort: fall back to the archive time */ }

			return new RadarScanInfo(scanTime, archive.ModeText);
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

		// Size of the leading range-request used to grab just the lowest tilt of a raw (modern) volume.
		// The 0.5° cut sits at the FILE START (metadata + surveillance + its Doppler companion + the
		// first radial of the next tilt, which proves the tilt is complete), so a few MB usually holds
		// it. 5 MB clears the biggest super-res split-cut tilts while being a fraction of a full
		// ~10-30 MB volume; if a tilt somehow doesn't fit, EnsureCachedAsync falls back to a full GET.
		private const int LowestTiltPrefixBytes = 5 * 1024 * 1024;

		// Parallel full-volume downloads during the tilt raw-prefetch. Deliberately lower than the live
		// chunk concurrency (8): these are ~10-30 MB each and run speculatively in the background, so they
		// must not starve the live-frame poll or the archive backfill the user is actually watching.
		private const int RawPrefetchConcurrency = 3;

		// Scan-mode line for an archive/replay frame, parsed from its cached single-tilt buffer's leading
		// metadata — the live SelectLatestSweep path doesn't run for archive frames, so this is how a
		// past-event loop gets its scan readout. Emits the SAME format as live ("VCP 212 · precip ·
		// 0.5°×3 · SAILS/MRLE ×2"), including the designed SAILS sweep count from the Message 5 elevation
		// table; falls back to VCP + regime alone if that count didn't parse. Null (rendered as "—") when
		// the VCP itself can't be read (a raw-fallback or legacy volume). (A 2011-era VCP 12 correctly
		// reads 0.5°×1 — no SAILS existed pre-2014 — which the UI shows as just "0.5°".)
		private static string? ModeTextFromTilt(byte[] tilt)
		{
			var (vcp, sweeps) = ReadModeFromExtractedTilt(tilt);
			if (!IsKnownVcp(vcp))
			{
				return null;
			}
			// Clamp mirrors the live path (SAILS tops out at ×3 = 4 base scans); an out-of-range count
			// means a misparse, so drop to VCP + regime rather than show a bogus "0.5°×9".
			return sweeps is >= 1 and <= 6 ? DescribeMode(vcp, sweeps) : DescribeVcp(vcp);
		}

		// Extracts ONE tilt from a decompressed AR2V volume: the base (lowest) tilt when tiltAngle is
		// null, else the cut at that angle. Two paths rather than one because they answer different
		// questions — the base path anchors on the first radial and needs no VCP table (so it works on a
		// legacy volume whose Message 5 won't parse), while targeting a specific tilt inherently requires
		// that table. Both yield the same KIND of buffer, so nothing downstream knows which ran.
		private static byte[]? ExtractTilt(byte[] data, string siteId, float? tiltAngle) =>
			tiltAngle is null
				? TryExtractLowestTilt(data, siteId)
				: TryExtractTiltByAngle(data, siteId, tiltAngle.Value, out _);

		public async Task<RadarVolume?> EnsureCachedAsync(RadarSite site, string key, float? tiltAngle = null, CancellationToken cancellationToken = default)
		{
			var time = ParseVolumeTime(key) ?? DateTimeOffset.UtcNow;
			var cacheFile = CacheFileFor(site.Id, time, tiltAngle);
			var localUrl = LocalUrlFor(site.Id, time, tiltAngle);

			// Reuse a tilt we've already fetched + extracted.
			if (File.Exists(cacheFile))
			{
				// Re-derive the scan mode + tilt list from the cached bytes (parse off the UI thread) so a
				// replay reload / site revisit still shows the VCP and offers tilts, though extraction is
				// skipped.
				string? cachedMode = null;
				IReadOnlyList<float>? cachedTilts = null;
				try
				{
					var cachedBytes = await File.ReadAllBytesAsync(cacheFile, cancellationToken);
					(cachedMode, cachedTilts) = await Task.Run(
						() => (ModeTextFromTilt(cachedBytes), ReadElevationAnglesFromExtractedTilt(cachedBytes)),
						cancellationToken);
				}
				catch (OperationCanceledException) { throw; }
				catch { /* both are best-effort; a bad read shows "—" and offers no tilt choice */ }
				return new RadarVolume(localUrl, site, time, cachedMode, cachedTilts, tiltAngle);
			}

			try
			{
				// Archived volumes (older dates) are gzip-wrapped (key ends ".gz"); the underlying
				// bytes are the same AR2V format the extractor expects, so gunzip first. Recent volumes
				// are stored raw, so the gunzip only runs for the historical Past Event Viewer fetches.
				var isGz = key.EndsWith(".gz", StringComparison.Ordinal);

				byte[]? toWrite = null;
				byte[]? fullVolume = null; // set only when we downloaded the whole thing (-> retain as .raw)

				// PREFETCHED RAW: one volume download holds EVERY tilt, so if the background prefetch has
				// already pulled this volume, any tilt is a local decompress — no network at all. This is
				// what makes switching tilts feel like switching products (see PrefetchRawVolumesAsync).
				var rawFile = RawCacheFileFor(site.Id, time);
				if (File.Exists(rawFile))
				{
					try
					{
						var rawBytes = await File.ReadAllBytesAsync(rawFile, cancellationToken);
						toWrite = await Task.Run(() => ExtractTilt(rawBytes, site.Id, tiltAngle), cancellationToken);

						// The raw IS the whole volume (and it's written atomically, so a file on disk is
						// complete). If the tilt isn't in it, the tilt does not exist — re-downloading the
						// same bytes to rediscover that would be pure waste. Say so now.
						if (toWrite is null && tiltAngle is not null)
						{
							RadarDiagnostics.Log("svc", "extract", ("site", site.Id), ("lvl", "warn"),
								("msg", $"tilt {tiltAngle:0.00}° not in cached volume {key}"));
							return null;
						}
					}
					catch (OperationCanceledException) { throw; }
					catch { /* a corrupt/unreadable raw just falls through to the network paths below */ }
				}

				// FAST PATH (base tilt, raw/modern volumes): the lowest tilt lives at the START of the
				// file, so range-GET just a leading prefix and extract from that — a few MB instead of the
				// whole ~10-30 MB volume, cutting download time for both first paint and every backfill
				// frame. Only when that prefix doesn't already hold a COMPLETE tilt (completedTilt) do we
				// fall back to the full download below. (.gz historical/legacy files aren't range-friendly
				// — a partial gzip stream can't be relied on — so they always take the full path.)
				//
				// A HIGHER tilt can't use this: it isn't at the file start, so the prefix wouldn't contain
				// it. Higher tilts go straight to the full download (or, above, the prefetched raw).
				if (toWrite is null && tiltAngle is null && !isGz)
				{
					var prefix = await TryGetRangeAsync(key, LowestTiltPrefixBytes, cancellationToken);
					if (prefix is not null)
					{
						toWrite = await Task.Run(() =>
						{
							try
							{
								var tilt = TryExtractLowestTilt(prefix, site.Id, out var completedTilt);
								return completedTilt ? tilt : null; // truncated prefix -> trigger a full download
							}
							catch
							{
								return null;
							}
						}, cancellationToken);
					}
				}

				// FULL DOWNLOAD: a higher tilt with no prefetched raw, a .gz file, a prefix too short to
				// hold the whole tilt, or a failed range request. Extract on a worker thread.
				//
				// On extraction failure the BASE tilt falls back to caching the whole volume — the JS
				// decoder reads that fine and its Math.min(elevations) still lands on the base tilt, so the
				// result is correct, just slower. That fallback is WRONG for a higher tilt and must not be
				// taken: the whole volume would decode to Math.min = the BASE tilt, silently painting 0.5°
				// while the combo reads 2.4°. Returning null instead lets the caller fall back to the base
				// tilt honestly (see LoadLoopCoreAsync). This is the path legacy .gz volumes take — they
				// gunzip to a fully-uncompressed AR2V with no bzip2 LDM records, which no tilt extraction
				// can walk, so they have no tilt selection at all.
				if (toWrite is null)
				{
					byte[] raw;
					using (var response = await _http.GetAsync(BucketBase + key, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
					{
						response.EnsureSuccessStatusCode();
						raw = await response.Content.ReadAsByteArrayAsync(cancellationToken);
					}
					var (extracted, volume) = await Task.Run<(byte[]?, byte[]?)>(() =>
					{
						try
						{
							var data = isGz ? Gunzip(raw) : raw;
							var tilt = ExtractTilt(data, site.Id, tiltAngle);
							return (tilt ?? (tiltAngle is null ? data : null), data);
						}
						catch (Exception ex)
						{
							System.Diagnostics.Debug.WriteLine($"[radar] {site.Id} extract failed, caching raw: {ex.Message}");
							return (tiltAngle is null ? raw : null, null);
						}
					}, cancellationToken);
					toWrite = extracted;
					fullVolume = volume;
				}

				if (toWrite is null)
				{
					// The volume genuinely has no cut at this angle (a VCP whose table and radials
					// disagree). Report it rather than caching a bogus file.
					RadarDiagnostics.Log("svc", "extract", ("site", site.Id), ("lvl", "warn"),
						("msg", $"no tilt {tiltAngle:0.00}° in {key}"));
					return null;
				}

				var temp = cacheFile + ".tmp";
				await File.WriteAllBytesAsync(temp, toWrite, cancellationToken);
				File.Move(temp, cacheFile, overwrite: true);

				// Having paid for the whole volume, keep it: every OTHER tilt is now a local extract. This
				// is the same bytes the prefetch would fetch, so retaining costs disk we'd have spent
				// anyway, and it means a tilt switch that outran the prefetch doesn't re-download. Skipped
				// for .gz (legacy volumes predate dual-pol/tilt interest and often don't extract at all).
				if (fullVolume is not null && !isGz)
				{
					await WriteRawAsync(rawFile, fullVolume, cancellationToken);
				}

				var mode = ModeTextFromTilt(toWrite); // VCP + regime for the archive/replay scan line
				var tilts = ReadElevationAnglesFromExtractedTilt(toWrite);
				return new RadarVolume(localUrl, site, time, mode, tilts, tiltAngle);
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

		// Writes a prefetched raw volume atomically (temp + move), best-effort: it's a pure optimization,
		// so a full disk or a lost race just means the next tilt extract re-downloads.
		private static async Task WriteRawAsync(string rawFile, byte[] volume, CancellationToken ct)
		{
			try
			{
				var temp = rawFile + ".tmp";
				await File.WriteAllBytesAsync(temp, volume, ct);
				File.Move(temp, rawFile, overwrite: true);
			}
			catch (OperationCanceledException) { throw; }
			catch { /* the raw cache is an optimization; failing to write it costs only a re-download */ }
		}

		/// <summary>
		/// Downloads each volume in full and retains it as a <c>.raw</c> cache entry, so that extracting
		/// any tilt later is a local decompress rather than a download. See the interface for the cost
		/// model and why this prefetches VOLUMES rather than tilts.
		/// </summary>
		public async Task PrefetchRawVolumesAsync(RadarSite site, IReadOnlyList<string> keys, CancellationToken cancellationToken = default)
		{
			var pending = keys.Where(k => !k.EndsWith(".gz", StringComparison.Ordinal)).ToList();
			if (pending.Count == 0)
			{
				return;
			}

			using var gate = new SemaphoreSlim(RawPrefetchConcurrency);
			var sw = System.Diagnostics.Stopwatch.StartNew();
			var fetched = 0;
			var bytes = 0L;

			await Task.WhenAll(pending.Select(async key =>
			{
				var time = ParseVolumeTime(key);
				if (time is null)
				{
					return;
				}
				var rawFile = RawCacheFileFor(site.Id, time.Value);
				if (File.Exists(rawFile))
				{
					return; // already prefetched (or retained by an earlier full download)
				}

				await gate.WaitAsync(cancellationToken);
				try
				{
					byte[] raw;
					using (var response = await _http.GetAsync(BucketBase + key, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
					{
						response.EnsureSuccessStatusCode();
						raw = await response.Content.ReadAsByteArrayAsync(cancellationToken);
					}
					await WriteRawAsync(rawFile, raw, cancellationToken);
					Interlocked.Increment(ref fetched);
					Interlocked.Add(ref bytes, raw.LongLength);
				}
				catch (OperationCanceledException) { throw; }
				catch (Exception ex)
				{
					// Per-volume and non-fatal: a missed raw just means that frame's tilt switch downloads.
					System.Diagnostics.Debug.WriteLine($"[radar] {site.Id} raw prefetch {key} failed: {ex.Message}");
				}
				finally
				{
					gate.Release();
				}
			}));

			RadarDiagnostics.Log("svc", "tilt.prefetch", ("site", site.Id),
				("volumes", fetched), ("mb", bytes / (1024 * 1024)), ("ms", (int)sw.ElapsedMilliseconds));
		}

		// GETs the first <paramref name="count"/> bytes of an S3 object via a Range request. Returns the
		// bytes (FEWER than count if the object is smaller — S3 then returns the whole object), or null
		// if the range request failed (server didn't honor it, network error) so the caller can fall back
		// to a full GET. 206 = partial (range honored); 200 = whole object (small file) — both usable.
		private async Task<byte[]?> TryGetRangeAsync(string key, int count, CancellationToken ct)
		{
			try
			{
				using var req = new HttpRequestMessage(HttpMethod.Get, BucketBase + key);
				req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, count - 1);
				using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
				if (resp.StatusCode != System.Net.HttpStatusCode.PartialContent &&
					resp.StatusCode != System.Net.HttpStatusCode.OK)
				{
					return null;
				}
				return await resp.Content.ReadAsByteArrayAsync(ct);
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch
			{
				return null; // fall back to a full GET
			}
		}

		// Safety cap on chunks decoded per live build. A plain volume is ~67 chunks, but a SAILS
		// precip VCP (extra 0.5° re-scans mid-volume) runs well past 90 — capping at 90 froze the
		// in-progress volume before its latest sweep's Doppler completed (the stuck partial-velocity
		// wedge). 160 covers a SAILS-heavy VCP 12/212/215 volume; decode stays cheap (incremental —
		// only NEW chunks are pulled/decompressed each poll).
		//
		// This cap is SAFE for tilt selection, and not by accident: chunks are taken in SEQUENCE order,
		// and the radar scans bottom-up, so the chunks this drops are always the END of the volume — the
		// HIGH tilts. Those are the ones we never serve live anyway (their best-case age has already
		// converged on the archive's). The low tilts a live loop offers are the earliest chunks and are
		// always inside the cap. If it ever needs raising, it's for SAILS, not for tilts.
		private const int LiveChunkCap = 160;

		// Live-frame build was the slowest radar op (~8-12 s) because each chunk was downloaded AND
		// bzip2-decoded serially — a fresh volume is dozens-to-hundreds of separate S3 GETs. They're
		// independent (order-independent: blocks are keyed by sequence), so overlap the round-trips +
		// the CPU-bound decode across this many workers, collapsing the build to network/decode-bound.
		private const int LiveDownloadConcurrency = 8;

		// Per-volume decoded-chunk cache so repeated polls of the same in-progress volume only
		// download + decompress the NEW chunks (bounds bandwidth to ~one volume's worth). Keyed
		// by site/volume; reset when the newest volume changes.
		private string? _liveVolKey;
		private byte[]? _liveHeader;
		// The actual per-radial ICAO of the current live volume, once resolved — a few radars write their
		// radials under a different callsign than the AWS key (the ROC test bed KCRI uses "NOK5"). Reset
		// with the volume; lets later polls tag chunk elevations with the right id from the start.
		private byte[]? _liveIcao;
		private readonly SortedDictionary<int, (byte[] block, int elev)> _liveBlocks = new();

		// One-shot cache of the previous-volume fallback frame (used while the newest volume is
		// still mid-scan). That volume is finished and immutable, so we build it once and reuse it
		// across the repeated polls instead of re-downloading ~a volume's worth of chunks each time.
		private string? _fallbackKey;
		private RadarVolume? _fallbackVolume;

		public async Task<RadarVolume?> GetLiveFrameAsync(RadarSite site, float? tiltAngle = null, CancellationToken cancellationToken = default)
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
				var frame = await BuildLiveFrameAsync(site, vol, start, useCache: true, tiltAngle, cancellationToken);
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
						// Keyed by TILT as well as volume: the same previous volume yields a different
						// frame per elevation, so without the tilt a switch would be served the previous
						// tilt's cached frame.
						var fbKey = $"{site.Id}/{prevVol}/{ps:yyyyMMddHHmmss}{TiltSuffix(tiltAngle)}";
						if (_fallbackKey == fbKey && _fallbackVolume is not null)
						{
							return _fallbackVolume;
						}
						RadarDiagnostics.Log("svc", "live", ("site", site.Id), ("vol", vol),
							("msg", $"newest not ready -> fall back to finished vol={prevVol}"));
						var fb = await BuildLiveFrameAsync(site, prevVol, ps, useCache: false, tiltAngle, cancellationToken);
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

		// Builds a single-tilt live frame from one chunk volume's latest complete sweep at tiltAngle
		// (null = the 0.5° base). When useCache, accumulates chunks across polls in the _liveVol* cache
		// (for the growing newest volume); otherwise it's a one-shot download into locals (for the
		// previous-volume fallback). Returns null if the volume has no S header chunk, or no complete
		// sweep at that tilt yet — normal early in a volume for a tilt the antenna hasn't reached.
		//
		// A higher tilt costs no extra network: the chunks for the whole in-progress volume are already
		// downloaded and decoded into _liveBlocks (that's the ~8-12 s build), so tilts 2-4 are extracted
		// from bytes we already hold.
		private async Task<RadarVolume?> BuildLiveFrameAsync(RadarSite site, string vol, DateTimeOffset start, bool useCache, float? tiltAngle, CancellationToken ct)
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
					_liveIcao = null;
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
			var siteIcao = System.Text.Encoding.ASCII.GetBytes(site.Id);
			// Reuse the resolved data ICAO across polls of the same live volume (the newest-volume path);
			// the fallback (previous finished volume) resolves fresh below. See _liveIcao.
			var icao = (useCache ? _liveIcao : null) ?? siteIcao;

			// Pick the missing chunks to pull this pass, in sequence order, bounded by the cap (the
			// cross-poll cache in `blocks` already holds prior polls' chunks). These are independent,
			// so we fetch + decode them in PARALLEL below instead of one HTTP round-trip at a time.
			var toFetch = new List<(string key, int seq, char kind)>();
			for (int i = 0; i < chunks.Count && blocks.Count + toFetch.Count < LiveChunkCap; i++)
			{
				if (!blocks.ContainsKey(chunks[i].seq)) toFetch.Add(chunks[i]);
			}

			// Fetch + bzip2-decode in parallel (bounded). Results land in a thread-safe bag and the
			// S chunk's 24-byte header is captured; the SortedDictionary `blocks` is NOT thread-safe,
			// so all mutation of it happens AFTER the parallel batch, back on this thread.
			var results = new ConcurrentBag<(int seq, byte[] block, int elev)>();
			byte[]? parsedHeader = null;
			var headerVanished = false;
			await Parallel.ForEachAsync(toFetch,
				new ParallelOptions { MaxDegreeOfParallelism = LiveDownloadConcurrency, CancellationToken = ct },
				async (c, token) =>
				{
					byte[] bytes;
					using (var resp = await _http.GetAsync(ChunksBase + c.key, HttpCompletionOption.ResponseHeadersRead, token))
					{
						if (!resp.IsSuccessStatusCode)
						{
							if (c.kind == 'S') headerVanished = true; // header chunk gone -> bail after the batch
							return; // a later chunk expired; use what we have
						}
						bytes = await resp.Content.ReadAsByteArrayAsync(token);
					}

					var isS = c.kind == 'S';
					if (isS && bytes.Length >= 24) parsedHeader = bytes[..24]; // single S chunk -> no race

					var block = DecompressChunk(bytes, isS);
					if (block is null) return;
					results.Add((c.seq, block, ElevationOf(block, icao)));
				});

			// Header vanished mid-read and we have no cached one -> can't decode (matches the old bail).
			if (headerVanished && header is null && parsedHeader is null) return null;

			// Merge the parallel results into the (possibly shared) block map on this thread.
			foreach (var r in results) blocks[r.seq] = (r.block, r.elev);
			var added = results.Count;
			if (parsedHeader is not null)
			{
				header = parsedHeader;
				if (useCache) _liveHeader = header;
			}

			if (header is null)
			{
				RadarDiagnostics.Log("svc", "live", ("site", site.Id), ("vol", vol),
					("msg", "header chunk missing -> can't decode"));
				return null;
			}

			// Resolve the real per-radial ICAO once. If the site id isn't present in a radial block, the
			// radar writes under a different callsign (KCRI -> "NOK5"); ElevationOf/SelectLatestSweep key on
			// it, so without this every block reads elev 0 and no cut is found (the "refl=False vel=False"
			// live fallback that leaves KCRI stuck ~10-13 min behind on archive frames). Detect it, cache it
			// for later polls, and recompute the blocks' elevations (they were tagged with the site id at
			// decode time). Normal sites contain the site id, so this never runs.
			if (icao.AsSpan().SequenceEqual(siteIcao))
			{
				foreach (var b in blocks.Values)
				{
					if (!HasMoment(b.block, Dref)) continue;
					if (IndexOf(b.block, siteIcao) < 0 && TryDetectIcao(b.block, out var real))
					{
						icao = real;
						if (useCache) _liveIcao = real;
						foreach (var k in blocks.Keys.ToList())
						{
							blocks[k] = (blocks[k].block, ElevationOf(blocks[k].block, icao));
						}
						RadarDiagnostics.Log("svc", "live", ("site", site.Id), ("vol", vol),
							("msg", $"data ICAO differs: using '{System.Text.Encoding.ASCII.GetString(real)}'"));
					}
					break; // probed one radial block — resolved (or the site id is genuinely present)
				}
			}

			var ordered = blocks.Values.ToList();
			var hdr = header;
			var sel = await Task.Run(() => SelectLatestSweep(hdr, ordered, icao, tiltAngle), ct);
			if (!sel.complete || sel.data is null || !sel.velComplete)
			{
				RadarDiagnostics.Log("svc", "live", ("site", site.Id), ("vol", vol),
					("msg", $"no complete sweep yet at {(tiltAngle is { } ta ? $"{ta:0.00}°" : "base")} " +
						$"(refl={sel.complete} vel={sel.velComplete}, {blocks.Count} chunks, +{added}) -> fall back"));
				return null;
			}

			// Trust the parsed radial time only if it's plausibly within this volume's life
			// (>= volume start, <= now); otherwise fall back to the volume-start timestamp so
			// a bad parse can never produce a bogus age.
			var nowUtc = DateTimeOffset.UtcNow;
			var ts = sel.dataTime is { } dt && dt >= start.AddMinutes(-2) && dt <= nowUtc.AddMinutes(2)
				? dt
				: start;
			var cacheFile = LiveCacheFileFor(site.Id, ts, tiltAngle);
			if (!File.Exists(cacheFile))
			{
				var temp = cacheFile + ".tmp";
				await File.WriteAllBytesAsync(temp, sel.data, ct);
				File.Move(temp, cacheFile, overwrite: true);
			}
			PruneLiveCache(site.Id, cacheFile);

			var mode = DescribeMode(sel.vcp, sel.sweeps);
			var tilts = ReadElevationAnglesFromExtractedTilt(sel.data);
			RadarDiagnostics.Log("svc", "live", ("site", site.Id), ("vol", vol),
				("builtZ", ts.ToUniversalTime().ToString("HH:mm:ss")),
				("ageMin", Math.Round((DateTimeOffset.UtcNow - ts).TotalMinutes, 1)),
				("mode", mode),
				("msg", $"BUILT latest {(tiltAngle is { } tb ? $"{tb:0.00}°" : "0.5°")} sweep ({blocks.Count} chunks)"));
			return new RadarVolume(LiveUrlFor(site.Id, ts, tiltAngle), site, ts, mode, tilts, tiltAngle);
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

		// Cache-name suffix identifying which TILT a buffer holds: "_e024" = 2.4°. The BASE tilt gets no
		// suffix, so its filename/URL are byte-identical to what shipped before tilt selection existed —
		// existing caches stay valid and the default view's paths are untouched. Tenths of a degree is
		// exact for the designed VCP angles (0.5, 0.9, 1.3, 1.8, 2.4, …) and distinguishes every tilt in
		// every WSR-88D VCP.
		private static string TiltSuffix(float? tiltAngle) =>
			tiltAngle is null ? string.Empty : $"_e{(int)Math.Round(tiltAngle.Value * 10):000}";

		private string CacheFileFor(string siteId, DateTimeOffset time, float? tiltAngle = null) =>
			Path.Combine(CacheDirectory, $"{siteId}_{time:yyyyMMdd_HHmmss}{TiltSuffix(tiltAngle)}.V06");

		private static string LocalUrlFor(string siteId, DateTimeOffset time, float? tiltAngle = null) =>
			$"https://{CacheHostName}/{siteId}_{time:yyyyMMdd_HHmmss}{TiltSuffix(tiltAngle)}.V06";

		// The prefetched RAW (still-compressed, all-tilts) volume. Its ".raw" extension keeps it out of
		// the "*.V06" globs that enumerate renderable tilts — the WebView must never fetch one (it holds
		// every tilt and isn't what the decoder expects), and the tilt prune must not treat it as a tilt.
		// It exists so that extracting a NEW tilt is a local decompress instead of a re-download: one
		// volume download contains every tilt, so this is the whole prefetch (see PrefetchRawVolumesAsync).
		private string RawCacheFileFor(string siteId, DateTimeOffset time) =>
			Path.Combine(CacheDirectory, $"{siteId}_{time:yyyyMMdd_HHmmss}.raw");

		// The "yyyyMMdd_HHmmss" stamp embedded in a cache filename, or null if it doesn't parse. Used by
		// the prune to group a volume's files (base tilt + every extracted tilt + the raw) under ONE
		// timestamp, so the keep-set can be per-volume rather than per-file.
		private static string? StampOf(string path, string siteId)
		{
			var name = Path.GetFileNameWithoutExtension(path);
			const int stampLength = 15; // yyyyMMdd_HHmmss
			var start = siteId.Length + 1;
			return name.Length >= start + stampLength && name.StartsWith(siteId, StringComparison.OrdinalIgnoreCase)
				? name.Substring(start, stampLength)
				: null;
		}

		// Live (chunks) frames get a "_live_" infix so they're served from the same host but
		// never confused with archive loop frames (and skipped by the archive prune below).
		private string LiveCacheFileFor(string siteId, DateTimeOffset time, float? tiltAngle = null) =>
			Path.Combine(CacheDirectory, $"{siteId}_live_{time:yyyyMMdd_HHmmss}{TiltSuffix(tiltAngle)}.V06");

		private static string LiveUrlFor(string siteId, DateTimeOffset time, float? tiltAngle = null) =>
			$"https://{CacheHostName}/{siteId}_live_{time:yyyyMMdd_HHmmss}{TiltSuffix(tiltAngle)}.V06";

		// Deletes this site's cached volumes that aren't in the current keep-set, so the loop cache
		// doesn't grow without bound. Live frames are pruned separately (PruneLiveCache) and excluded.
		//
		// The keep-set is per-VOLUME (by the timestamp embedded in the key), NOT per-file: one volume now
		// owns several files — the base tilt, any extracted higher tilts ("_e024"), and the prefetched
		// ".raw" — which all live or die together. Matching whole filenames (as this did before tilts)
		// would have kept only the base tilt and deleted every extracted tilt on the very next refresh.
		private void PruneCache(string siteId, IReadOnlyList<string> keepKeys)
		{
			try
			{
				var keep = new HashSet<string>(
					keepKeys.Select(k => ParseVolumeTime(k))
						.Where(t => t is not null)
						.Select(t => t!.Value.ToString("yyyyMMdd_HHmmss")),
					StringComparer.OrdinalIgnoreCase);

				foreach (var file in Directory.EnumerateFiles(CacheDirectory, $"{siteId}_*")
					.Where(f => f.EndsWith(".V06", StringComparison.OrdinalIgnoreCase)
						|| f.EndsWith(".raw", StringComparison.OrdinalIgnoreCase)))
				{
					if (file.Contains("_live_", StringComparison.OrdinalIgnoreCase))
					{
						continue; // owned by PruneLiveCache
					}
					var stamp = StampOf(file, siteId);
					if (stamp is null || !keep.Contains(stamp))
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
