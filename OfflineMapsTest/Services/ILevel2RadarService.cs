using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OfflineMapsTest.Models;

namespace OfflineMapsTest.Services
{
	/// <summary>
	/// Fetches and caches NEXRAD Level II volumes (lowest-tilt reflectivity, one file per
	/// volume timestamp) from the public AWS archive bucket. All HTTP happens here (C# side,
	/// avoiding the WebView's CORS limits); this service never touches WebView2. MainWindow
	/// maps <see cref="CacheDirectory"/> to a WebView virtual host so the page can read each
	/// cached file and decode it in JS.
	/// </summary>
	public interface ILevel2RadarService
	{
		/// <summary>Absolute path of the on-disk volume cache folder.</summary>
		string CacheDirectory { get; }

		/// <summary>
		/// Returns the keys of the most recent <paramref name="count"/> volumes for the site,
		/// oldest-first, and prunes cached volumes outside that set. Cheap (listing only).
		/// </summary>
		Task<IReadOnlyList<string>> GetRecentKeysAsync(RadarSite site, int count, CancellationToken cancellationToken = default);

		/// <summary>
		/// Ensures the volume for <paramref name="key"/> is downloaded + lowest-tilt-extracted
		/// to the cache (reusing it if already present), returning its overlay descriptor, or
		/// null if the fetch failed.
		/// </summary>
		Task<RadarVolume?> EnsureCachedAsync(RadarSite site, string key, CancellationToken cancellationToken = default);
	}
}
