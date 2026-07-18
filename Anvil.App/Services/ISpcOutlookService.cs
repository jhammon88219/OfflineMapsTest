using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// Fetches, caches, and exposes SPC severe- and fire-weather outlook products as
	/// local GeoJSON for the map to load later. HTTP fetching happens here (C# side,
	/// avoiding the WebView's CORS limits); this service never touches WebView2. The
	/// view (MainWindow) maps <see cref="CacheDirectory"/> to a WebView virtual host so
	/// the cached files are reachable by the page.
	/// </summary>
	public interface ISpcOutlookService
	{
		/// <summary>Absolute path of the on-disk GeoJSON cache folder. The view maps
		/// this to a WebView virtual host; the service itself never does.</summary>
		string CacheDirectory { get; }

		/// <summary>All known outlook products (immutable catalog projection).</summary>
		IReadOnlyList<SpcOutlookProduct> Products { get; }

		/// <summary>Distinct outlook days that have at least one product (ascending).</summary>
		IReadOnlyList<int> AvailableDays { get; }

		/// <summary>Products valid for the given outlook day — drives the dependent
		/// Day+Product selectors the UI will add later.</summary>
		IReadOnlyList<SpcOutlookProduct> GetProductsForDay(int day);

		/// <summary>Resolves a (day, type) selection to its product (carrying the local
		/// cache URL), or null if that combination isn't a valid product.</summary>
		SpcOutlookProduct? Resolve(int day, SpcOutlookType type);

		/// <summary>
		/// Reads the issued / valid / expire times from the product's cached GeoJSON, or
		/// null if the file is missing or carries no risk areas. These reflect the actual
		/// data on disk (so they stay correct even when the cache is a refresh behind).
		/// </summary>
		SpcOutlookTimes? GetTimesForProduct(SpcOutlookProduct product);

		/// <summary>
		/// Fetches every product and writes each to the cache (one GeoJSON per product).
		/// Conditional GETs skip unchanged outlooks; the last-known-good file is kept on
		/// failure; per-product failures are isolated and reported via the results
		/// rather than thrown.
		/// </summary>
		Task<IReadOnlyList<SpcOutlookFetchResult>> RefreshAllAsync(CancellationToken cancellationToken = default);

		/// <summary>
		/// Fetches the SPC forecast-discussion narrative text for the product's outlook (the
		/// prose from the SPC HTML page, scraped from its &lt;pre&gt; block and cached on disk).
		/// One narrative covers all of a day's hazard sub-products. Returns null when the product
		/// has no supported narrative page (fire-weather is not wired yet) or fetching fails with
		/// no cached copy. The last-known-good text is returned on a network failure.
		/// </summary>
		Task<string?> GetNarrativeAsync(SpcOutlookProduct product, CancellationToken cancellationToken = default);
	}
}
