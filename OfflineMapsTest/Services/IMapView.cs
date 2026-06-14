using System.Threading.Tasks;

namespace OfflineMapsTest.Services
{
	/// <summary>
	/// The seam between the rest of the app and the WebView2-hosted map. Hides all
	/// WebView2 / JavaScript details from the services.
	/// </summary>
	public interface IMapView
	{
		/// <summary>
		/// Runs the given JavaScript against the hosted map and returns its result.
		/// Only valid once the map has finished initializing.
		/// </summary>
		Task<string> RunScriptAsync(string javaScript);
	}
}
