using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SharpCompress.Compressors;
using SharpCompress.Compressors.BZip2;

namespace OfflineMapsTest.Services
{
	/// <summary>
	/// NEXRAD Level II byte-format reader: the pure, stateless knowledge of how to read a
	/// DECOMPRESSED AR2V volume — Message-31 radial primitives (elevation, moments, VCP, collection
	/// time), the latest-sweep selection for the live chunks, and the lowest-tilt archive extraction.
	/// Extracted from Level2RadarService, which is left with fetch / cache / list / orchestrate. All
	/// members are static; diagnostics go to the static RadarDiagnostics (same namespace).
	/// </summary>
	internal static class Level2Format
	{
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
		internal static (byte[]? data, bool complete, bool velComplete, DateTimeOffset? dataTime, int sweeps, int vcp)
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
		internal const int CtmHeaderSize = 12;
		internal const int MessageHeaderSize = 16;
		internal const int RadarDataSize = 2432; // fixed frame size for non-Message-31 records

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
		internal static int ReadVcpFromMetadata(List<(byte[] block, int elev)> blocks)
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
		internal static int ReadSailsSweepsFromMetadata(List<(byte[] block, int elev)> blocks, float observedRefAngle)
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
		internal static int ReadVcp(byte[] block, byte[] icao)
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
		internal static DateTimeOffset? ReadCollectionTime(byte[] block, byte[] icao)
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
		internal static readonly HashSet<int> ClearAirVcps = new() { 31, 32, 35, 90 };
		internal static readonly HashSet<int> PrecipVcps = new() { 11, 12, 21, 112, 121, 211, 212, 215, 221 };

		internal static bool IsKnownVcp(int vcp) => ClearAirVcps.Contains(vcp) || PrecipVcps.Contains(vcp);

		// Maps the VCP number to a human label. Clear-air VCPs scan ~every 10 min and never use
		// SAILS; precip VCPs (12/212/215/…) run ~4-6 min and may insert extra 0.5° sweeps. An
		// unrecognized number means the parse failed -> "VCP ?" (no category, since we can't tell).
		internal static string DescribeMode(int vcp, int sweeps)
		{
			if (!IsKnownVcp(vcp))
			{
				return $"VCP ? · 0.5°×{sweeps}";
			}
			var clearAir = ClearAirVcps.Contains(vcp);
			var sails = sweeps > 1 ? $" · SAILS/MRLE ×{sweeps - 1}" : "";
			return $"VCP {vcp} · {(clearAir ? "clear-air" : "precip")} · 0.5°×{sweeps}{sails}";
		}

		// VCP + regime only (no sweep count) — the archive/replay mode line, where per-frame we read
		// the VCP from the cached tilt's metadata but not (yet) the sweep count. Empty when the VCP
		// isn't recognized, so the caller shows "—" rather than a bogus "VCP ?".
		internal static string DescribeVcp(int vcp)
		{
			if (!IsKnownVcp(vcp))
			{
				return string.Empty;
			}
			return $"VCP {vcp} · {(ClearAirVcps.Contains(vcp) ? "clear-air" : "precip")}";
		}

		// Reads the scan mode from an ALREADY-EXTRACTED single-tilt buffer (a cached .V06: 24-byte
		// AR2V header + decompressed records) — used for archive/replay frames, whose EnsureCachedAsync
		// path never runs the live SelectLatestSweep. Treats the whole buffer (minus the 24-byte header)
		// as one metadata "block": the leading metadata record's Message 5 sits at a 2432-byte stride
		// from that offset, and both readers stop at the first radial (Message 31), so the real Message 5
		// is found before any radial data. Returns (0,0) — rendered as "—" — for a raw/unparseable
		// fallback buffer. `sweeps` is parsed too (designed SAILS count from Message 5) for the mode's
		// second tier; refAngle is passed NaN here (no observed base angle handy on this path), which
		// skips ReadSailsSweepsFromMetadata's reality-check and trusts the table.
		internal static (int vcp, int sweeps) ReadModeFromExtractedTilt(byte[] tilt)
		{
			const int headerSize = 24;
			if (tilt is null || tilt.Length <= headerSize)
			{
				return (0, 0);
			}
			var one = new List<(byte[] block, int elev)> { (tilt[headerSize..], 0) };
			var vcp = ReadVcpFromMetadata(one);
			var sweeps = ReadSailsSweepsFromMetadata(one, float.NaN);
			return (vcp, sweeps);
		}

		// Builds a minimal uncompressed volume containing ONLY the lowest elevation's records.
		// See the design notes in the previous revision: 24-byte header + decompressed LDM
		// records up to the first elevation-2 record (records align to elevations, lowest
		// first). Returns null if nothing parses (caller caches the raw bytes).
		internal static byte[]? TryExtractLowestTilt(byte[] raw, string siteId) =>
			TryExtractLowestTilt(raw, siteId, out _);

		// As above, but also reports whether the lowest tilt is definitely COMPLETE — i.e. we
		// reached a radial of the next (higher-angle) tilt, proving tilt 1 was fully scanned.
		// The archive path always sees a full volume, so the flag is true there.
		internal static byte[]? TryExtractLowestTilt(byte[] raw, string siteId, out bool completedTilt)
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
					// A real split cut is only TWO cuts: the surveillance and its Doppler companion. Once we've
						// kept the companion, ANOTHER elevation-number increase we can't confirm as the same
						// angle (the angle byte at ICAO+24 reads NaN on some sites' blocks) is the next tilt —
						// stop rather than keep piling on. Without this bound, a bad angle read ran the "base
						// tilt" up to elev 4-5 and bloated the cache to 20-27 MB (slow to fetch + extract +
						// JS-decode, and too big for the range prefix, forcing a full re-download). The lowest
						// tilt is already complete here (both halves captured).
						if (keptDoppler)
						{
							completedTilt = true;
							break;
						}
						baseElev = elev; // first same-angle split-cut Doppler companion -> keep it, advance marker
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

		internal static int ElevationOf(byte[] block, byte[] icao)
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
		internal static readonly byte[] Dref = System.Text.Encoding.ASCII.GetBytes("DREF");
		internal static readonly byte[] Dvel = System.Text.Encoding.ASCII.GetBytes("DVEL");

		// Elevation ANGLE (degrees) from the first Message 31 radial header in a block: a 4-byte
		// big-endian float at ICAO+24. Unlike the elevation NUMBER, this is the same for the base
		// 0.5° cut and its SAILS re-scan, which is how we recognize the re-scan. NaN if not found.
		internal static float ElevationAngleOf(byte[] block, byte[] icao)
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
		internal static bool HasMoment(byte[] block, byte[] name)
		{
			var p = IndexOf(block, name);
			if (p < 0 || p + 10 > block.Length)
			{
				return false;
			}
			var gates = (block[p + 8] << 8) | block[p + 9];
			return gates is >= 1 and <= 2000;
		}

		internal static int IndexOf(byte[] haystack, byte[] needle)
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
	}
}
