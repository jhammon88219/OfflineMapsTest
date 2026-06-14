using System.Collections.Generic;
using OfflineMapsTest.Models;

namespace OfflineMapsTest.Services
{
	/// <summary>
	/// Default <see cref="IRegionProvider"/>. Hardcodes the available regions for now.
	/// </summary>
	public sealed class RegionProvider : IRegionProvider
	{
		public IReadOnlyList<MapRegion> GetRegions() => new[]
		{
			// Id, DisplayName, Lng, Lat, Zoom, InsetZoom, MinZoom, MaxZoom, West, South, East, North
			// InsetZoom is the wider framing used when a region sits in a small inset
			// box; starting values — adjust and relaunch to retune.
			new MapRegion("conus", "Continental US", -96.18, 38.64, 4.89, 2.3, 4.89, 15, -128, 23, -65, 50),
			new MapRegion("alaska", "Alaska", -149.29, 63.28, 4.41, 1.7, 4.41, 15, -172, 51, -129, 72),
			new MapRegion("hawaii", "Hawaii", -157.42, 20.57, 7.96, 4.0, 7.96, 15, -161, 18, -154, 23),
		};
	}
}
