using System.Collections.Generic;
using OfflineMapsTest.Models;

namespace OfflineMapsTest.Services
{
	/// <summary>
	/// Enumerates the curated DOW (mobile-radar) frames bundled with the app for the DOW Event
	/// Viewer. Frames are offline-curated <c>.dow.json</c> files (produced by <c>tools/dow_import.py</c>)
	/// under <c>Assets/DowEvents/</c>; the list is empty until events are added.
	/// </summary>
	public interface IDowEventProvider
	{
		/// <summary>The bundled DOW frames (may be empty).</summary>
		IReadOnlyList<DowEvent> GetEvents();
	}
}
