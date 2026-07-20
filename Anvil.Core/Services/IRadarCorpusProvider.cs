using System.Collections.Generic;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// Supplies the fixed velocity-dealias validation corpus (the committed <c>.V06</c> volumes +
	/// their baselines) to the dev-only regression harness. Reads the bundled manifest; stays fully
	/// offline. See <see cref="RadarCorpusProvider"/> and docs/radar-validation.md.
	/// </summary>
	public interface IRadarCorpusProvider
	{
		/// <summary>The corpus volumes, in manifest order. Empty if the manifest is missing/unparseable.</summary>
		IReadOnlyList<RadarCorpusEntry> GetEntries();
	}
}
