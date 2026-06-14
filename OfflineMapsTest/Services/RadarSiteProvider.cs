using System.Collections.Generic;
using OfflineMapsTest.Models;

namespace OfflineMapsTest.Services
{
	/// <summary>
	/// Default <see cref="IRadarSiteProvider"/>. A curated subset of WSR-88D sites for v1;
	/// expand toward the full ~160-site list later. Coordinates are the published antenna
	/// locations. Hardcoded for now, like <see cref="RegionProvider"/>.
	/// </summary>
	public sealed class RadarSiteProvider : IRadarSiteProvider
	{
		public IReadOnlyList<RadarSite> GetSites() => new[]
		{
			// Id, Name, Latitude, Longitude, Zoom (single-site view ~230 km range)
			new RadarSite("KTLX", "Oklahoma City, OK", 35.3331, -97.2778, 7.5),
			new RadarSite("KFWS", "Dallas / Fort Worth, TX", 32.5731, -97.3031, 7.5),
			new RadarSite("KEAX", "Kansas City, MO", 38.8103, -94.2645, 7.5),
			new RadarSite("KLOT", "Chicago, IL", 41.6044, -88.0843, 7.5),
			new RadarSite("KOKX", "New York City, NY", 40.8656, -72.8639, 7.5),
			new RadarSite("KMLB", "Melbourne, FL", 28.1131, -80.6544, 7.5),
			new RadarSite("KFFC", "Atlanta, GA", 33.3636, -84.5658, 7.5),
			new RadarSite("KIWA", "Phoenix, AZ", 33.2892, -111.6700, 7.5),
			new RadarSite("KMUX", "San Francisco Bay Area, CA", 37.1551, -121.8984, 7.5),
			new RadarSite("KATX", "Seattle, WA", 48.1947, -122.4956, 7.5),
			new RadarSite("KBMX", "Birmingham, AL", 33.1722, -86.7697, 7.5),
			new RadarSite("KLWX", "Washington, DC", 38.9753, -77.4778, 7.5),
		};
	}
}
