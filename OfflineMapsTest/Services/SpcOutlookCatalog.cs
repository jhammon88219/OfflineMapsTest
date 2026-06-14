using System.Collections.Generic;
using OfflineMapsTest.Models;

namespace OfflineMapsTest.Services
{
	/// <summary>
	/// One row of the SPC endpoint table: which product, which day, where to fetch it.
	/// <see cref="FallbackUrl"/> is tried only if <see cref="PrimaryUrl"/> fails.
	/// </summary>
	public record SpcOutlookEndpoint(
		string Id,
		int Day,
		SpcOutlookType Type,
		string DisplayName,
		string PrimaryUrl,
		string? FallbackUrl);

	/// <summary>
	/// THE EDITABLE ENDPOINT TABLE. All SPC source URLs live here, one row per product,
	/// so URLs can be corrected in one place without touching fetch logic.
	///
	/// Defaults use SPC's own "cake layer" (.lyr.geojson) latest-named files. Rows
	/// marked VERIFY have filenames/params not confirmed live (chiefly fire weather and
	/// the IEM fallback query string); if a fetch 404s, fix the URL here. IEM's outlook
	/// service is the documented fallback.
	/// </summary>
	public static class SpcOutlookCatalog
	{
		private const string Spc = "https://www.spc.noaa.gov/products/outlook/";
		private const string SpcExp = "https://www.spc.noaa.gov/products/exper/day4-8/";

		// Fire weather isn't published as GeoJSON on spc.noaa.gov (images only), so it
		// comes from NOAA's ArcGIS SPC_firewx MapServer as a per-layer GeoJSON query.
		// Each day is a group layer; the main outlook area is its FIRST sub-layer
		// (Day 1 = 1, Day 2 = 4, then +3 per day: 7, 10, 13, 16, 19, 22). The second
		// sub-layer in each group (2, 5, 8, ...) is the "dry thunderstorm" area — a
		// possible future product. outSR=4326 forces WGS84 lon/lat for MapLibre.
		private const string ArcFire = "https://mapservices.weather.noaa.gov/vector/rest/services/fire_weather/SPC_firewx/MapServer/";
		private const string ArcQuery = "/query?where=1%3D1&outFields=*&outSR=4326&f=geojson";

		public static IReadOnlyList<SpcOutlookEndpoint> All { get; } = new[]
		{
			// ---- Convective categorical (Day 1-3) ------------------------------
			new SpcOutlookEndpoint("day1-categorical", 1, SpcOutlookType.Categorical, "Day 1 Categorical", Spc + "day1otlk_cat.lyr.geojson", null),
			new SpcOutlookEndpoint("day2-categorical", 2, SpcOutlookType.Categorical, "Day 2 Categorical", Spc + "day2otlk_cat.lyr.geojson", null),
			new SpcOutlookEndpoint("day3-categorical", 3, SpcOutlookType.Categorical, "Day 3 Categorical", Spc + "day3otlk_cat.lyr.geojson", null),

			// ---- Convective probabilistic tornado/wind/hail (Day 1-2) ----------
			new SpcOutlookEndpoint("day1-tornado", 1, SpcOutlookType.Tornado, "Day 1 Tornado", Spc + "day1otlk_torn.lyr.geojson", null),
			new SpcOutlookEndpoint("day1-wind",    1, SpcOutlookType.Wind,    "Day 1 Wind",    Spc + "day1otlk_wind.lyr.geojson", null),
			new SpcOutlookEndpoint("day1-hail",    1, SpcOutlookType.Hail,    "Day 1 Hail",    Spc + "day1otlk_hail.lyr.geojson", null),
			new SpcOutlookEndpoint("day2-tornado", 2, SpcOutlookType.Tornado, "Day 2 Tornado", Spc + "day2otlk_torn.lyr.geojson", null),
			new SpcOutlookEndpoint("day2-wind",    2, SpcOutlookType.Wind,    "Day 2 Wind",    Spc + "day2otlk_wind.lyr.geojson", null),
			new SpcOutlookEndpoint("day2-hail",    2, SpcOutlookType.Hail,    "Day 2 Hail",    Spc + "day2otlk_hail.lyr.geojson", null),

			// ---- Convective combined probabilistic (Day 3) ---------------------
			new SpcOutlookEndpoint("day3-probabilistic", 3, SpcOutlookType.ProbabilisticCombined, "Day 3 Probabilistic", Spc + "day3otlk_prob.lyr.geojson", null),

			// ---- Convective extended probabilistic (Day 4-8) -------------------
			new SpcOutlookEndpoint("day4-probabilistic", 4, SpcOutlookType.ExtendedProbabilistic, "Day 4 Probabilistic", SpcExp + "day4prob.lyr.geojson", null),
			new SpcOutlookEndpoint("day5-probabilistic", 5, SpcOutlookType.ExtendedProbabilistic, "Day 5 Probabilistic", SpcExp + "day5prob.lyr.geojson", null),
			new SpcOutlookEndpoint("day6-probabilistic", 6, SpcOutlookType.ExtendedProbabilistic, "Day 6 Probabilistic", SpcExp + "day6prob.lyr.geojson", null),
			new SpcOutlookEndpoint("day7-probabilistic", 7, SpcOutlookType.ExtendedProbabilistic, "Day 7 Probabilistic", SpcExp + "day7prob.lyr.geojson", null),
			new SpcOutlookEndpoint("day8-probabilistic", 8, SpcOutlookType.ExtendedProbabilistic, "Day 8 Probabilistic", SpcExp + "day8prob.lyr.geojson", null),

			// ---- Fire weather (Day 1-2) — NOAA ArcGIS SPC_firewx MapServer (GeoJSON)
			new SpcOutlookEndpoint("day1-fire", 1, SpcOutlookType.FireWeather, "Day 1 Fire Weather", ArcFire + "1" + ArcQuery, null),
			new SpcOutlookEndpoint("day2-fire", 2, SpcOutlookType.FireWeather, "Day 2 Fire Weather", ArcFire + "4" + ArcQuery, null),

			// ---- Extended fire weather (Day 3-8) — same MapServer, later layers ----
			new SpcOutlookEndpoint("day3-fire", 3, SpcOutlookType.ExtendedFireWeather, "Day 3 Fire Weather", ArcFire + "7" + ArcQuery, null),
			new SpcOutlookEndpoint("day4-fire", 4, SpcOutlookType.ExtendedFireWeather, "Day 4 Fire Weather", ArcFire + "10" + ArcQuery, null),
			new SpcOutlookEndpoint("day5-fire", 5, SpcOutlookType.ExtendedFireWeather, "Day 5 Fire Weather", ArcFire + "13" + ArcQuery, null),
			new SpcOutlookEndpoint("day6-fire", 6, SpcOutlookType.ExtendedFireWeather, "Day 6 Fire Weather", ArcFire + "16" + ArcQuery, null),
			new SpcOutlookEndpoint("day7-fire", 7, SpcOutlookType.ExtendedFireWeather, "Day 7 Fire Weather", ArcFire + "19" + ArcQuery, null),
			new SpcOutlookEndpoint("day8-fire", 8, SpcOutlookType.ExtendedFireWeather, "Day 8 Fire Weather", ArcFire + "22" + ArcQuery, null),
		};
	}
}
