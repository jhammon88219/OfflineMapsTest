using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using OfflineMapsTest.Models;

namespace OfflineMapsTest.Services
{
	/// <summary>
	/// Default <see cref="IRadarSiteProvider"/>. Loads the full WSR-88D network (~160 sites)
	/// from the bundled <c>Assets/radar-sites.json</c> data file (id/name/lat/lon), which is
	/// regenerable from NOAA's published station table rather than hand-curated in code. Stays
	/// fully offline — the network is effectively static. Parsed once and cached; falls back to a
	/// tiny built-in set if the file is missing or unparseable so the picker never comes up empty.
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
			try
			{
				var path = Path.Combine(AppContext.BaseDirectory, "Assets", "radar-sites.json");
				var json = File.ReadAllText(path);
				var dtos = JsonSerializer.Deserialize<List<SiteDto>>(json, JsonOptions);
				if (dtos is { Count: > 0 })
				{
					return dtos
						.Where(d => !string.IsNullOrWhiteSpace(d.Id))
						.Select(d => new RadarSite(d.Id!, d.Name ?? d.Id!, d.Lat, d.Lon))
						.ToList();
				}
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"[radar] radar-sites.json load failed: {ex.Message}");
			}

			return Fallback;
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
