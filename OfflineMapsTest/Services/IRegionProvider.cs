using System.Collections.Generic;
using OfflineMapsTest.Models;

namespace OfflineMapsTest.Services
{
	/// <summary>
	/// Supplies the set of map regions the user can jump between.
	/// </summary>
	public interface IRegionProvider
	{
		IReadOnlyList<MapRegion> GetRegions();
	}
}
