using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// Default <see cref="IRadarSiteProvider"/>. Loads the full WSR-88D network (~160 sites)
	/// from the bundled <c>Assets/radar-sites.json</c> data file (id/name/lat/lon), which is
	/// regenerable from NOAA's published station table rather than hand-curated in code, PLUS the
	/// research/test radars from <c>Assets/research-radar-sites.json</c> (tagged
	/// <see cref="RadarSiteClass.Research"/>). Both merge into one <see cref="GetSites"/> list — a
	/// research radar is just another selectable site, only surfaced behind the "Show Research
	/// Radars" toggle. Stays fully offline — the network is effectively static. Parsed once and
	/// cached; falls back to a tiny built-in set if the operational file is missing or unparseable
	/// so the picker never comes up empty.
	///
	/// Also PLUS the FAA Terminal Doppler Weather Radars from <c>Assets/tdwr-sites.json</c> (tagged
	/// <see cref="RadarSiteClass.Tdwr"/>, revealed by the "Show TDWRs" toggle). TDWR volumes are the same
	/// Archive Level II family (AR2V0008) as the WSR-88D, so a TDWR loads/renders through the same
	/// pipeline; only the markers are surfaced separately.
	///
	/// <para><b>How the research/TDWR lists are derived (do-it-right, mirroring the NEXRAD list):</b> a
	/// site is "real" to us only if it publishes decodable standard Level II. We enumerate every
	/// site ID in the <c>unidata-nexrad-level2</c> archive bucket for a recent day and diff it
	/// against the operational 160. The extras are OCONUS operational WSR-88Ds (already listed), the
	/// ROC test radar <b>KCRI</b> (the one WSR-88D-format research radar in the feed → research file),
	/// and the <b>TDWRs</b> (the <c>T***</c> ids classified <c>stationType=TDWR</c> by the NWS API →
	/// TDWR file; note some <c>T***</c> ids like <c>TJUA</c> are actually WSR-88Ds and stay in the
	/// operational list). TDWR antenna coordinates come from each site's own volume header (the NWS
	/// API's are rounded to ~1 km, too coarse for gate projection). Regenerate via
	/// <c>tools</c> if NOAA changes the networks; the pipeline handles new sites with zero code change.</para>
	/// </summary>
	public sealed class RadarSiteProvider : IRadarSiteProvider
	{
		private static readonly JsonSerializerOptions JsonOptions = new()
		{
			PropertyNameCaseInsensitive = true,
		};

		private readonly Lazy<IReadOnlyList<RadarSite>> _sites = new(LoadSites);

		public IReadOnlyList<RadarSite> GetSites() => _sites.Value;

		private static IReadOnlyList<RadarSite> LoadSites()
		{
			var operational = LoadFile("radar-sites.json", RadarSiteClass.Operational) ?? Fallback;

			// Merge in the extra networks (research/test + TDWR), each skipping any id already covered
			// (belt-and-suspenders: the extra files should never duplicate an operational id — the
			// dedupe keeps the categories mutually exclusive, e.g. a T-prefixed WSR-88D wins as operational).
			var merged = new List<RadarSite>(operational);
			var known = new HashSet<string>(operational.Select(s => s.Id), StringComparer.OrdinalIgnoreCase);

			foreach (var extra in new[]
			{
				LoadFile("research-radar-sites.json", RadarSiteClass.Research),
				LoadFile("tdwr-sites.json", RadarSiteClass.Tdwr),
			})
			{
				if (extra is { Count: > 0 })
				{
					merged.AddRange(extra.Where(s => known.Add(s.Id)));
				}
			}

			return merged;
		}

		// Parse one bundled site data file into RadarSites, tagging each with its network. Returns null
		// (not an empty list) on any failure so the caller can distinguish "missing/bad file" from "empty".
		private static IReadOnlyList<RadarSite>? LoadFile(string fileName, RadarSiteClass siteClass)
		{
			try
			{
				var path = Path.Combine(AppContext.BaseDirectory, "Assets", fileName);
				var json = File.ReadAllText(path);
				var dtos = JsonSerializer.Deserialize<List<SiteDto>>(json, JsonOptions);
				if (dtos is { Count: > 0 })
				{
					return dtos
						.Where(d => !string.IsNullOrWhiteSpace(d.Id))
						.Select(d => new RadarSite(d.Id!, d.Name ?? d.Id!, d.Lat, d.Lon, siteClass))
						.ToList();
				}
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"[radar] {fileName} load failed: {ex.Message}");
			}

			return null;
		}

		// Used only if the bundled data file can't be read — keeps the map usable.
		private static readonly IReadOnlyList<RadarSite> Fallback = new[]
		{
			new RadarSite("KTLX", "Oklahoma City, OK", 35.3331, -97.2778),
			new RadarSite("KFWS", "Dallas / Fort Worth, TX", 32.5731, -97.3031),
			new RadarSite("KLOT", "Chicago, IL", 41.6044, -88.0843),
			new RadarSite("KOKX", "New York City, NY", 40.8656, -72.8639),
		};

		private sealed class SiteDto
		{
			public string? Id { get; set; }
			public string? Name { get; set; }
			public double Lat { get; set; }
			public double Lon { get; set; }
		}
	}
}
