using System.Threading;
using System.Threading.Tasks;

namespace Anvil.Services
{
	/// <summary>
	/// Fetches and caches the active, storm-based NWS Tornado / Severe Thunderstorm WARNING polygons
	/// (the modern forecaster-drawn polygons, not county areas) as local GeoJSON for the map. HTTP
	/// happens here (C# side, avoiding the WebView's CORS limits); this service never touches WebView2.
	/// The view (MainWindow) maps <see cref="CacheDirectory"/> to a WebView virtual host so the page can
	/// fetch the cached file. Sibling of <see cref="ISpcWatchService"/> — watches are the large outlook
	/// areas; warnings are the imminent-threat polygons, so they get their own layer + toggle.
	/// </summary>
	public interface IWarningService
	{
		/// <summary>Absolute path of the on-disk GeoJSON cache folder. The view maps this to a
		/// WebView virtual host; the service itself never does.</summary>
		string CacheDirectory { get; }

		/// <summary>The local virtual-host URL the page loads the cached warning GeoJSON from.</summary>
		string WarningsUrl { get; }

		/// <summary>
		/// Fetches the warning polygons and writes the currently-active ones to the cache. The
		/// last-known-good file is kept on failure; failures are reported via the result rather
		/// than thrown.
		/// </summary>
		Task<WarningFetchResult> RefreshAsync(CancellationToken cancellationToken = default);
	}

	/// <summary>Outcome of a single <see cref="IWarningService.RefreshAsync"/> call.</summary>
	public enum WarningFetchStatus { Updated, FailedCacheKept, FailedNoCache }

	/// <summary>Result of a refresh: status, the number of active warnings written (total + per phenom for
	/// the UI readout), and a message. Per-type counts are meaningful only on <see cref="WarningFetchStatus.Updated"/>.</summary>
	public sealed record WarningFetchResult(WarningFetchStatus Status, int ActiveCount = 0, int TornadoCount = 0, int SevereCount = 0, string? Message = null);
}
