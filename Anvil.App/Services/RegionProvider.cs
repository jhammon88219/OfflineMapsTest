using System.Collections.Generic;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// Default <see cref="IRegionProvider"/>. Hardcodes the available regions for now.
	/// </summary>
	public sealed class RegionProvider : IRegionProvider
	{
		public IReadOnlyList<MapRegion> GetRegions() => new[]
		{
			// Id, DisplayName, Lng, Lat, Zoom
			new MapRegion("conus", "Continental US", -96.18, 38.64, 4.89),
		};
	}
}
