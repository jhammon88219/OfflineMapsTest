using System.Collections.Generic;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// Supplies the radar sites available in the picker. Mirrors <see cref="IRegionProvider"/>
	/// / <see cref="IStyleProvider"/>.
	/// </summary>
	public interface IRadarSiteProvider
	{
		IReadOnlyList<RadarSite> GetSites();
	}
}
