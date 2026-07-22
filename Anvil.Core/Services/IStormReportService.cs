using System;
using System.Threading;
using System.Threading.Tasks;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// Fetches, caches, and exposes SPC storm reports (the filtered Tornado / Wind / Hail reports SPC uses
	/// to verify its outlooks) as local GeoJSON points for the map. HTTP happens here (C# side, avoiding the
	/// WebView's CORS limits); this service never touches WebView2. The view (MainWindow) maps
	/// <see cref="CacheDirectory"/> to a WebView virtual host so the page can fetch the cached file.
	///
	/// Reports are keyed by the SPC "convective day" (12Z→12Z), the same window an outlook is valid over —
	/// so a day's reports line up with that day's outlook for at-a-glance verification. A HISTORICAL day is
	/// immutable and fetched once; TODAY's file grows through the day, so the NowCast refresh re-fetches it.
	/// </summary>
	public interface IStormReportService
	{
		/// <summary>Absolute path of the on-disk GeoJSON cache folder. The view maps this to a WebView
		/// virtual host; the service itself never does.</summary>
		string CacheDirectory { get; }

		/// <summary>The <c>stormreports</c>-host URL the page loads a cached day's report points from.</summary>
		string LocalUrl(DateOnly convectiveDay);

		/// <summary>
		/// Ensures the given convective day's storm reports are cached as one GeoJSON point collection
		/// (each feature tagged <c>kind</c> = torn/wind/hail so the page can filter by type). Fetches SPC's
		/// per-type CSVs (the deduped "filtered" file, falling back to the raw file for pre-~2012 dates that
		/// predate filtered) and transforms them; the last-known-good file is kept on a failed fetch. SPC
		/// truncates each remark to ~160 chars, so we also pull the day's IEM LSRs and swap in the full
		/// narrative where a report matches one (best-effort — unmatched dots keep the SPC snippet).
		/// <paramref name="immutable"/> = true (a historical day) reuses the cache without re-fetching;
		/// false (today) always re-fetches, since the day's reports accumulate.
		/// </summary>
		Task<StormReportResult> EnsureReportsAsync(DateOnly convectiveDay, bool immutable, CancellationToken cancellationToken = default);
	}
}
