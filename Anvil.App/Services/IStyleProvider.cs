using System.Collections.Generic;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// Supplies the set of map styles the user can choose between.
	/// </summary>
	public interface IStyleProvider
	{
		IReadOnlyList<MapStyle> GetStyles();
	}
}
