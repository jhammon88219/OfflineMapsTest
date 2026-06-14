using System.Collections.Generic;
using OfflineMapsTest.Models;

namespace OfflineMapsTest.Services
{
	/// <summary>
	/// Supplies the set of map styles the user can choose between.
	/// </summary>
	public interface IStyleProvider
	{
		IReadOnlyList<MapStyle> GetStyles();
	}
}
