using System.Collections.Generic;
using OfflineMapsTest.Models;

namespace OfflineMapsTest.Services
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
