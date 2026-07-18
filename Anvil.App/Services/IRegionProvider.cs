using System.Collections.Generic;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// Supplies the set of map regions the user can jump between.
	/// </summary>
	public interface IRegionProvider
	{
		IReadOnlyList<MapRegion> GetRegions();
	}
}
