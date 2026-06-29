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

		// From the decoded records, picks the LATEST complete lowest-tilt SURVEILLANCE (reflectivity)
		// sweep — the freshest base scan available. In SAILS precip VCPs the 0.5° tilt is re-scanned
		// mid-volume under its own, HIGHER elevation NUMBER but at the same elevation ANGLE, so we
		// must key on angle, not number (keying on elevation-number 1 only ever found the volume-start
		// scan and missed the SAILS re-scans — the "stuck several minutes behind during an outbreak"
		// bug, verified against live KILX/KSHV volumes). We also require the cut to be a SURVEILLANCE
		// cut (reflectivity present, NO velocity): split-cut precip VCPs scan 0.5° twice — a long-PRT
		// surveillance cut (the reflectivity we render) and a short-PRT Doppler cut (velocity + range-
		// folded reflectivity we don't want), at the same angle but different elevation numbers; the
		// Doppler one carries a velocity moment, the surveillance one doesn't. Clear-air VCPs have a
		// single combined cut at the bottom, so when no velocity-free cut exists we fall back to the
		// latest low-tilt reflectivity cut. Returns the minimal single-tilt buffer (24-byte header +
		// leading metadata records + that cut's radials), its actual radial time, the count of such
		// low-tilt sweeps (SAILS indicator), and the VCP number.
		private static (byte[]? data, bool complete, bool velComplete, DateTimeOffset? dataTime, int sweeps, int vcp)
			SelectLatestSweep(byte[] header, List<(byte[] block, int elev)> blocks, byte[] icao)
		{
			var firstRadial = blocks.FindIndex(b => b.elev >= 1);
			if (firstRadial < 0)
			{
				return (null, false, false, null, 0, 0);
			}

			// Authoritative VCP from the leading metadata record's Message 5. Fall back to the
			// best-effort Message-31 VOL-block read only if Message 5 didn't yield a real VCP.
			var vcp = ReadVcpFromMetadata(blocks);
			if (!IsKnownVcp(vcp))
			{
				var radialVcp = ReadVcp(blocks[firstRadial].block, icao);
				if (IsKnownVcp(radialVcp))
				{
					vcp = radialVcp;
				}
			}

			// Group consecutive same-elevation-NUMBER records into cuts, capturing each cut's
			// elevation ANGLE (median over its records — ignores the few settling radials at a
			// cut's start) and which moments it carries (reflectivity / velocity).
			var cuts = new List<(int start, int end, float angle, bool hasRef, bool hasVel)>();
			var i = firstRadial;
			while (i < blocks.Count)
			{
				var start = i;
				var num = blocks[i].elev;
				var angles = new List<float>();
				var hasRef = false;
				var hasVel = false;
				while (i < blocks.Count && blocks[i].elev == num)
				{
					var a = ElevationAngleOf(blocks[i].block, icao);
					if (!float.IsNaN(a))
					{
						angles.Add(a);
					}
					hasRef |= HasMoment(blocks[i].block, Dref);
					hasVel |= HasMoment(blocks[i].block, Dvel);
					i++;
				}
				angles.Sort();
				var angle = angles.Count > 0 ? angles[angles.Count / 2] : float.NaN;
				cuts.Add((start, i, angle, hasRef, hasVel));
			}

			// Lowest tilt = the minimum angle among reflectivity cuts (the 0.5° base; a SAILS
			// re-scan shares that exact angle). A cut at that tilt is the same physical 0.5° scan.
			const float tiltTol = 0.12f; // SAILS re-scan reads the base angle within antenna jitter
			var refCuts = cuts.Where(c => c.hasRef && !float.IsNaN(c.angle)).ToList();
			if (refCuts.Count == 0)
			{
				return (null, false, false, null, 0, vcp);
			}
			var refAngle = refCuts.Min(c => c.angle);

			bool LowTilt((int start, int end, float angle, bool hasRef, bool hasVel) c)
				=> c.hasRef && !float.IsNaN(c.angle) && Math.Abs(c.angle - refAngle) <= tiltTol;

			// Prefer surveillance cuts (reflectivity, no velocity); clear-air has one combined cut.
			var surveillance = cuts.Where(c => LowTilt(c) && !c.hasVel).ToList();
			var pool = surveillance.Count > 0 ? surveillance : cuts.Where(LowTilt).ToList();

			// Designed 0.5° base-scan count from the VCP's elevation table (Message 5) — the PLANNED
			// SAILS count, which the observed completed-cut count (pool.Count) under-reports mid-volume
			// because the re-scans haven't run yet when we poll a still-scanning live volume. Validated
			// against refAngle (its lowest tilt must match the actually-observed one) so a bad parse
			// can't override with a plausible-but-wrong number. Prefer it when sane; clamp to a
			// plausible range (SAILS tops out at ×3 = 4 base scans); else fall back to observed.
			var designedSweeps = ReadSailsSweepsFromMetadata(blocks, refAngle);
			var sweeps = designedSweeps is >= 1 and <= 6 ? designedSweeps : pool.Count;

			// Complete = terminated by a later cut (the antenna moved on); the trailing in-progress
			// cut of a live volume is excluded, so we never serve a half-scanned sweep.
			var ready = pool.Where(c => c.end < blocks.Count).ToList();
			if (ready.Count == 0)
			{
				return (null, false, false, null, sweeps, vcp);
			}

			// Pick the cut to render and emit its PAIRED Doppler (velocity) cut so the decoded volume
			// carries both moments. In a split-cut precip VCP the 0.5° tilt is the surveillance cut
			// (reflectivity, no velocity) immediately followed by its Doppler companion (velocity), at
			// the same angle but the next elevation NUMBER. Writing the companion right AFTER the
			// surveillance keeps the surveillance the LOWER number, so the JS `Math.min(elevations)`
			// still picks it for reflectivity and the higher-numbered Doppler supplies velocity.
			// Clear-air VCPs have one combined cut (it carries velocity itself), so no companion.
			//
			// Pick the surveillance cut (reflectivity) to serve and its paired Doppler cut (velocity).
			// We prefer the LATEST surveillance whose Doppler companion is a COMPLETE, full-circle
			// sweep. Chasing the freshest SAILS re-scan instead serves whatever fraction of its Doppler
			// has scanned so far, and because the chunk cap freezes the in-progress volume and a
			// same-timestamp refresh is skipped, that partial wedge (e.g. a ~90° quarter circle) never
			// fills in — the broken frame in the log/screenshot (`vel 240rad span120`). Stepping back
			// at most one sub-scan (~1-3 min) to a complete pair keeps FULL velocity AND full
			// reflectivity. Only if NO surveillance yet has a complete companion do we fall back to the
			// freshest partial one, so the newest frame still shows velocity rather than going blank.
			// The companion is the adjacent same-angle velocity cut, written AFTER the surveillance so
			// its higher elevation number leaves the JS reflectivity pick (Math.min) on the surveillance.
			var selected = ready[^1];
			(int start, int end)? velCut = null;

			// The Doppler companion immediately after a surveillance cut, if any — identified by
			// VELOCITY at the SAME angle (NOT reflectivity: a short-PRT Doppler's range-folded
			// reflectivity can fail the HasMoment gate-count check). `requireComplete` additionally
			// demands a terminated, full sweep (a later cut follows it).
			(int start, int end)? DopplerCompanion(int surveillanceStart, bool requireComplete)
			{
				var si = cuts.FindIndex(c => c.start == surveillanceStart);
				if (si < 0 || si + 1 >= cuts.Count) return null;
				var next = cuts[si + 1];
				var sameAngle = !float.IsNaN(next.angle) && Math.Abs(next.angle - refAngle) <= tiltTol;
				if (!(sameAngle && next.hasVel) || (requireComplete && next.end >= blocks.Count))
				{
					return null; // not a same-angle velocity cut, or (when required) still mid-scan
				}
				return (next.start, next.end);
			}

			if (surveillance.Count > 0)
			{
				foreach (var requireComplete in new[] { true, false })
				{
					for (var s = ready.Count - 1; s >= 0; s--)
					{
						if (DopplerCompanion(ready[s].start, requireComplete) is { } comp)
						{
							selected = ready[s];
							velCut = comp;
							break;
						}
					}

					if (velCut is not null)
					{
						break;
					}
				}
			}

			var dataTime = ReadCollectionTime(blocks[selected.start].block, icao);

			using var output = new MemoryStream(8 * 1024 * 1024);
			output.Write(header, 0, header.Length);
			for (var m = 0; m < firstRadial; m++) // leading metadata (Msg 5/13/15/18/…) the decoder needs
			{
				output.Write(blocks[m].block, 0, blocks[m].block.Length);
			}
			for (var k = selected.start; k < selected.end; k++) // surveillance (reflectivity) cut
			{
				output.Write(blocks[k].block, 0, blocks[k].block.Length);
			}
			if (velCut is { } vc) // paired Doppler (velocity) cut, written after -> higher elevation
			{
				for (var k = vc.start; k < vc.end; k++)
				{
					output.Write(blocks[k].block, 0, blocks[k].block.Length);
				}
			}

			// Velocity is "complete" when its full sweep is present: a split-cut's paired Doppler is a
			// terminated (not still-scanning) cut, OR — for clear-air — the selected combined cut itself
			// carries velocity. A partial Doppler (a still-scanning wedge, e.g. 120 of 720 radials) is
			// NOT complete; the live builder uses this to fall back to the previous (finished) volume
			// rather than serve a mostly-empty velocity frame.
			var velComplete = surveillance.Count == 0
				? selected.hasVel
				: velCut is { } vchk && vchk.end < blocks.Count;
			RadarDiagnostics.Log("svc", "sweep",
				("icao", System.Text.Encoding.ASCII.GetString(icao)), ("vcp", vcp),
				("velComplete", velComplete),
				("msg", $"sweeps={sweeps} (obs={pool.Count} designed={designedSweeps}) refCuts={refCuts.Count} surv={surveillance.Count} @ {refAngle:0.00}° " +
					$"sel=[{selected.start}..{selected.end}]({selected.end - selected.start}blk,{selected.angle:0.00}°) " +
					$"vel={(velCut is { } vlog ? $"[{vlog.start}..{vlog.end}]({vlog.end - vlog.start}blk,{(velComplete ? "complete" : "PARTIAL")})" : "NONE")} " +
					$"blocks={blocks.Count} t={(dataTime is { } d3 ? d3.ToString("HH:mm:ss") : "?")}"));

			return (output.ToArray(), true, velComplete, dataTime, sweeps, vcp);
		}

		// Level II message framing within a decompressed record (per the format ICD, matching
		// the vendored decoder's constants): every non-Message-31 record is a fixed 2432-byte
		// frame made of a 12-byte legacy CTM header + a 16-byte message header + the body. So
		// the message-type byte sits at frame offset 12+3 = 15, and a message body begins at
		// frame offset 12+16 = 28.
		private const int CtmHeaderSize = 12;
		private const int MessageHeaderSize = 16;
		private const int RadarDataSize = 2432; // fixed frame size for non-Message-31 records

		// Authoritative VCP from Message 5 (RDA Volume Coverage Pattern Data), which lives in the
		// volume's leading metadata record. That record holds only non-Message-31 messages, so
		// they sit at exact 2432-byte strides; the radial data (Message 31) is variable-length and
		// always comes AFTER the metadata. So walk blocks in order at 2432 strides, stop the moment
		// a Message-31 (radial) frame appears — Message 5 can't be past it, and the stride no longer
		// holds in radial data — and otherwise return the first type-5 (or its reserved twin type-7)
		// message's pattern_number: the 3rd halfword of the body (message_size, pattern_type,
		// pattern_number, …), at frame offset 28+4 = 32. Returns 0 if no recognizable VCP is found.
		//
		// NOTE: this deliberately does NOT key off `firstRadial` / block elevations. The metadata
		// record contains the ICAO string at offsets whose byte-22 can read as a bogus elevation,
		// so ElevationOf flags a metadata block as a radial and `firstRadial` collapses to 0 — which
		// is exactly what made an earlier firstRadial-gated version silently scan nothing.
		private static int ReadVcpFromMetadata(List<(byte[] block, int elev)> blocks)
		{
			foreach (var (block, _) in blocks)
			{
				for (var pos = 0; pos + CtmHeaderSize + MessageHeaderSize + 6 <= block.Length; pos += RadarDataSize)
				{
					var msgType = block[pos + CtmHeaderSize + 3];
					if (msgType == 31)
					{
						return 0; // reached radial data; the leading metadata (with Message 5) is behind us
					}
					if (msgType is not (5 or 7))
					{
						continue;
					}
					var body = pos + CtmHeaderSize + MessageHeaderSize;
					var vcp = (block[body + 4] << 8) | block[body + 5];
					if (IsKnownVcp(vcp))
					{
						return vcp;
					}
				}
			}
			return 0;
		}

		// Number of DESIGNED 0.5° base scans (the SAILS count) from the VCP's elevation table in
		// Message 5, which lists every planned cut up front — so it reports SAILS×N even when polling
		// a still-scanning volume whose re-scans haven't run yet (where counting completed radial cuts
		// reads ×1). Same metadata walk as ReadVcpFromMetadata. Message 5 body (after the 12-byte CTM
		// + 16-byte message headers): pattern_number at +4, num_elevations (int16) at +6, then a
		// 22-byte VCP header; each elevation cut is a fixed 46-byte block starting at +22, whose
		// elevation_angle is the leading int16 (coded as value/8*0.043945°) and whose waveform_type is
		// the byte at cut+3 (1 = CS surveillance). A split-cut precip VCP lists the 0.5° tilt as paired
		// CS+CD cuts at the same angle; counting the SURVEILLANCE (waveform 1) cuts at the lowest angle
		// matches the "surveillance sweeps" we render. Clear-air VCPs list one cut per elevation (no CS
		// pairing), so fall back to counting all lowest-angle cuts there. Returns 0 if not parseable,
		// or if the table's lowest angle doesn't match <paramref name="observedRefAngle"/> (the actual
		// observed 0.5° tilt) — that mismatch means the byte offsets didn't line up, so the caller
		// keeps the trustworthy observed count rather than a plausible-but-wrong override. Field
		// offsets/encoding verified against the vendored nexrad-level-2-data Message 5 parser.
		private static int ReadSailsSweepsFromMetadata(List<(byte[] block, int elev)> blocks, float observedRefAngle)
		{
			foreach (var (block, _) in blocks)
			{
				for (var pos = 0; pos + CtmHeaderSize + MessageHeaderSize + 8 <= block.Length; pos += RadarDataSize)
				{
					var msgType = block[pos + CtmHeaderSize + 3];
					if (msgType == 31)
					{
						return 0; // reached the radial data; Message 5 is behind us
					}
					if (msgType is not (5 or 7))
					{
						continue;
					}

					var body = pos + CtmHeaderSize + MessageHeaderSize;
					var vcp = (block[body + 4] << 8) | block[body + 5];
					if (!IsKnownVcp(vcp))
					{
						continue;
					}

					var numElev = (short)((block[body + 6] << 8) | block[body + 7]);
					if (numElev is <= 0 or > 40)
					{
						return 0;
					}

					const int cutsStart = 22, stride = 46;
					var angles = new List<(double angle, int waveform)>(numElev);
					for (var k = 0; k < numElev; k++)
					{
						var off = body + cutsStart + k * stride;
						if (off + 4 > block.Length)
						{
							break;
						}
						var raw = (short)((block[off] << 8) | block[off + 1]);
						angles.Add((raw / 8.0 * 0.043945, block[off + 3]));
					}
					if (angles.Count == 0)
					{
						return 0;
					}

					const double tol = 0.25;
					var minAngle = angles.Min(a => a.angle);
					// Reality check: the designed lowest tilt must match the observed one, or the parse
					// is misaligned — bail so the caller keeps the observed count.
					if (!float.IsNaN(observedRefAngle) && Math.Abs(minAngle - observedRefAngle) > 0.3)
					{
						return 0;
					}
					var cs = angles.Count(a => Math.Abs(a.angle - minAngle) < tol && a.waveform == 1);
					return cs >= 1 ? cs : angles.Count(a => Math.Abs(a.angle - minAngle) < tol);
				}
			}
			return 0;
		}

		// VCP number from a Message 31 radial's Volume Data Constant Block (the "VOL" block),
		// reached via the block pointer at ICAO+32; VCP is a 2-byte int at VOL+40. Best-effort
		// fallback for the rare volume whose Message 5 doesn't parse (see ReadVcpFromMetadata).
		private static int ReadVcp(byte[] block, byte[] icao)
		{
			var ic = IndexOf(block, icao);
			if (ic < 0 || ic + 36 > block.Length)
			{
				return 0;
			}
			var volPtr = (block[ic + 32] << 24) | (block[ic + 33] << 16) | (block[ic + 34] << 8) | block[ic + 35];
			var pos = ic + volPtr + 40;
			if (volPtr <= 0 || pos + 2 > block.Length)
			{
				return 0;
			}
			return (block[pos] << 8) | block[pos + 1];
		}

		// Radial collection time from a Message 31 header: ms-since-midnight (ICAO+4, 4 bytes) and
		// modified Julian date (ICAO+8, 2 bytes; day 1 = 1970-01-01).
		private static DateTimeOffset? ReadCollectionTime(byte[] block, byte[] icao)
		{
			var ic = IndexOf(block, icao);
			if (ic < 0 || ic + 10 > block.Length)
			{
				return null;
			}
			long ms = ((long)block[ic + 4] << 24) | ((long)block[ic + 5] << 16) | ((long)block[ic + 6] << 8) | block[ic + 7];
			var julian = (block[ic + 8] << 8) | block[ic + 9];
			if (julian <= 0 || ms < 0 || ms > 86_400_000)
			{
				return null;
			}
			return new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(julian - 1).AddMilliseconds(ms);
		}

		// Real WSR-88D volume coverage patterns. Used to validate the (best-effort) VCP parse:
		// anything outside this set is a bad read, shown as "VCP ?" rather than a wrong number.
		private static readonly HashSet<int> ClearAirVcps = new() { 31, 32, 35, 90 };
		private static readonly HashSet<int> PrecipVcps = new() { 11, 12, 21, 112, 121, 211, 212, 215, 221 };

		private static bool IsKnownVcp(int vcp) => ClearAirVcps.Contains(vcp) || PrecipVcps.Contains(vcp);

		// Maps the VCP number to a human label. Clear-air VCPs scan ~every 10 min and never use
		// SAILS; precip VCPs (12/212/215/…) run ~4-6 min and may insert extra 0.5° sweeps. An
		// unrecognized number means the parse failed -> "VCP ?" (no category, since we can't tell).
		private static string DescribeMode(int vcp, int sweeps)
		{
			if (!IsKnownVcp(vcp))
			{
				return $"VCP ? · 0.5°×{sweeps}";
			}
			var clearAir = ClearAirVcps.Contains(vcp);
			var sails = sweeps > 1 ? $" · SAILS/MRLE ×{sweeps - 1}" : "";
			return $"VCP {vcp} · {(clearAir ? "clear-air" : "precip")} · 0.5°×{sweeps}{sails}";
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

		public async Task<IReadOnlyCollection<string>> GetLiveSiteIdsAsync(CancellationToken cancellationToken = default)
		{
			var now = DateTimeOffset.UtcNow;
			var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			// today + yesterday so a site isn't falsely flagged offline in the first minutes of
			// the UTC day (when today's prefix is still nearly empty for everyone).
			foreach (var day in new[] { now, now.AddDays(-1) })
			{
				try
				{
					await AddSitesForDayAsync(day, live, cancellationToken);
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch
				{
					// Best effort: a failed listing just leaves those sites unflagged.
				}
			}
			return live;
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

		// Builds a minimal uncompressed volume containing ONLY the lowest elevation's records.
		// See the design notes in the previous revision: 24-byte header + decompressed LDM
		// records up to the first elevation-2 record (records align to elevations, lowest
		// first). Returns null if nothing parses (caller caches the raw bytes).
		private static byte[]? TryExtractLowestTilt(byte[] raw, string siteId) =>
			TryExtractLowestTilt(raw, siteId, out _);

		// As above, but also reports whether the lowest tilt is definitely COMPLETE — i.e. we
		// reached a radial of the next (higher-angle) tilt, proving tilt 1 was fully scanned.
		// The archive path always sees a full volume, so the flag is true there.
		private static byte[]? TryExtractLowestTilt(byte[] raw, string siteId, out bool completedTilt)
		{
			completedTilt = false;
			const int headerSize = 24;
			if (raw.Length < headerSize + 4)
			{
				return null;
			}

			var icao = System.Text.Encoding.ASCII.GetBytes(siteId);
			using var output = new MemoryStream(8 * 1024 * 1024);
			output.Write(raw, 0, headerSize);

			// Keep the whole lowest tilt by ANGLE, not by elevation NUMBER. A split-cut precip VCP
			// scans 0.5° as TWO cuts at the SAME angle — a surveillance cut (elevation number 1,
			// reflectivity) immediately followed by its Doppler companion (number 2, velocity).
			// Stopping at number >= 2 (as this used to) dropped the velocity cut, so every archive
			// loop frame was blank in Velocity mode. Instead we anchor the base angle on the first
			// real radial and keep every radial at that angle (both halves of the split cut), stopping
			// only at a radial that belongs to a genuinely higher tilt. Clear-air VCPs have one
			// combined 0.5° cut that itself carries velocity and a higher-angle next tilt, so the same
			// rule keeps exactly that one cut. The companion is written AFTER the surveillance, so its
			// higher elevation number leaves the JS `Math.min(elevations)` picking the surveillance for
			// reflectivity while the Doppler cut supplies velocity.
			const float tiltTol = 0.20f; // the Doppler companion shares the base angle within jitter
			var pos = headerSize;
			var records = 0;
			var inTilt1 = false;       // have we reached the first real 0.5° radial yet?
			var baseAngle = float.NaN; // settled angle of the base tilt (the MAX over its radials)
			var baseTiltNum = 0;       // the base tilt's elevation NUMBER (its split-cut pair shares it)
			var baseElev = 0;          // the highest elevation NUMBER accepted as still the base tilt
			var keptDoppler = false;   // did we keep a higher-numbered same-angle (Doppler) cut?
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
				try
				{
					using var blockIn = new MemoryStream(raw, pos, size, writable: false);
					using var bz = new BZip2Stream(blockIn, CompressionMode.Decompress, false);
					using var blockOut = new MemoryStream(1024 * 1024);
					bz.CopyTo(blockOut);
					block = blockOut.ToArray();
				}
				catch
				{
					break; // a malformed record: stop and serve what we have rather than failing
				}
				pos += size;

				var elev = ElevationOf(block, icao);
				var angle = ElevationAngleOf(block, icao);

				if (!inTilt1)
				{
					// Leading metadata (Msg 5/13/15/…) the decoder needs, plus any settling radials,
					// up to the first real reflectivity radial. We anchor on a block that actually
					// carries a DREF moment (metadata never does), so a false ICAO match in metadata —
					// whose bytes can read as a plausible elevation/angle — can't anchor us early.
					if (!float.IsNaN(angle) && elev >= 1 && HasMoment(block, Dref))
					{
						inTilt1 = true;
						baseAngle = angle;
						baseTiltNum = elev;
						baseElev = elev;
					}
					output.Write(block, 0, block.Length);
					records++;
					continue;
				}

				// Track the base tilt's SETTLED angle as the max over its own radials. The first
				// radial of a cut is a settling radial that can read well below the true angle (a real
				// 0.5° base read as 0.27°); anchoring on it alone made the threshold too tight and
				// dropped the Doppler cut — the intermittent "vel none" archive frames in the log.
				if (elev == baseTiltNum && !float.IsNaN(angle) && angle > baseAngle)
				{
					baseAngle = angle;
				}

				// Only evaluate the tilt boundary at an elevation-NUMBER increase. A stray angle
				// misread on a block WITHIN the cut (a bad ICAO+24 float read) must not abort the
				// sweep — doing so truncated reflectivity to ~one block and dropped the Doppler cut,
				// the intermittent ~120-radial blank-frame bug. A genuinely higher tilt always carries
				// a higher elevation number, so gate on that first, then confirm by angle. Over-keeping
				// (a higher tilt slipping through) is harmless — the JS picks reflectivity from the min
				// elevation and velocity from the first velocity cut — but dropping the Doppler is not.
				if (elev > baseElev)
				{
					if (!float.IsNaN(angle) && angle > baseAngle + tiltTol)
					{
						completedTilt = true; // crossed into a higher tilt -> tilt 1 fully scanned
						break;
					}
					baseElev = elev; // same-angle split-cut Doppler companion -> keep it, advance marker
					keptDoppler = true;
				}

				output.Write(block, 0, block.Length);
				records++;
			}

			RadarDiagnostics.Log("svc", "extract", ("site", siteId),
				("records", records), ("baseElev", baseElev),
				("keptDoppler", keptDoppler), ("completedTilt", completedTilt),
				("bytes", records > 0 ? output.Length : 0),
				("msg", $"baseAngle={(float.IsNaN(baseAngle) ? "?" : baseAngle.ToString("0.00"))}°"));

			return records > 0 ? output.ToArray() : null;
		}

		private static int ElevationOf(byte[] block, byte[] icao)
		{
			var i = IndexOf(block, icao);
			if (i < 0 || i + 22 >= block.Length)
			{
				return 0;
			}

			// Elevation number is a 1-based index into the VCP's elevation list (1..~25). Anything
			// outside a plausible range is a false ICAO match inside non-radial (metadata) data, so
			// report 0 ("not a radial") rather than a bogus tilt boundary.
			var elev = block[i + 22];
			return elev is >= 1 and <= 32 ? elev : 0;
		}

		// Moment-block markers: a Message 31 generic data-moment block starts with a 'D' type byte
		// + 3-char moment name. SAILS detection keys on reflectivity-vs-velocity presence.
		private static readonly byte[] Dref = System.Text.Encoding.ASCII.GetBytes("DREF");
		private static readonly byte[] Dvel = System.Text.Encoding.ASCII.GetBytes("DVEL");

		// Elevation ANGLE (degrees) from the first Message 31 radial header in a block: a 4-byte
		// big-endian float at ICAO+24. Unlike the elevation NUMBER, this is the same for the base
		// 0.5° cut and its SAILS re-scan, which is how we recognize the re-scan. NaN if not found.
		private static float ElevationAngleOf(byte[] block, byte[] icao)
		{
			var i = IndexOf(block, icao);
			if (i < 0 || i + 28 > block.Length)
			{
				return float.NaN;
			}
			return System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(block.AsSpan(i + 24, 4));
		}

		// Whether a generic data-moment block (e.g. "DREF" reflectivity, "DVEL" velocity) is present
		// with a plausible gate count (1..2000) — the count is a u16 eight bytes after the name. The
		// gate-count check rejects coincidental ASCII matches in non-moment data.
		private static bool HasMoment(byte[] block, byte[] name)
		{
			var p = IndexOf(block, name);
			if (p < 0 || p + 10 > block.Length)
			{
				return false;
			}
			var gates = (block[p + 8] << 8) | block[p + 9];
			return gates is >= 1 and <= 2000;
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
