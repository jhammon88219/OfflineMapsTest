using System.Threading;
using System.Threading.Tasks;

namespace OfflineMapsTest.Services
{
	/// <summary>
	/// Fetches and caches the active SPC convective watch boxes (Tornado / Severe Thunderstorm
	/// Watches) as local GeoJSON for the map. HTTP happens here (C# side, avoiding the WebView's
	/// CORS limits); this service never touches WebView2. The view (MainWindow) maps
	/// <see cref="CacheDirectory"/> to a WebView virtual host so the page can fetch the cached file.
	/// </summary>
	public interface ISpcWatchService
	{
		/// <summary>Absolute path of the on-disk GeoJSON cache folder. The view maps this to a
		/// WebView virtual host; the service itself never does.</summary>
		string CacheDirectory { get; }

		/// <summary>The local virtual-host URL the page loads the cached watch GeoJSON from.</summary>
		string WatchesUrl { get; }

		/// <summary>
		/// Fetches the SPC watch polygons and writes the currently-active ones to the cache. The
		/// last-known-good file is kept on failure; failures are reported via the result rather
		/// than thrown.
		/// </summary>
		Task<SpcWatchFetchResult> RefreshAsync(CancellationToken cancellationToken = default);
	}

	/// <summary>Outcome of a single <see cref="ISpcWatchService.RefreshAsync"/> call.</summary>
	public enum SpcWatchFetchStatus { Updated, FailedCacheKept, FailedNoCache }

	/// <summary>Result of a refresh: status, the number of active watches written, and a message.</summary>
	public sealed record SpcWatchFetchResult(SpcWatchFetchStatus Status, int ActiveCount = 0, string? Message = null);
}
