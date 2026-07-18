using System.Threading;
using System.Threading.Tasks;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// Resolves the user's current location. Two independent methods are exposed — OS geolocation
	/// (accurate, needs the location capability + consent) and an IP-based lookup (approximate,
	/// needs network) — plus <see cref="ResolveAsync"/> which tries the OS first and falls back to
	/// IP. Each method returns <c>null</c> on failure (denied consent, no fix, offline) rather than
	/// throwing, so callers can simply chain to the next method.
	/// </summary>
	public interface ILocationService
	{
		/// <summary>
		/// Asks the operating system for the device location. Returns <c>null</c> if access is
		/// denied or no fix is available. The result's <see cref="UserLocation.Source"/> is
		/// <see cref="LocationSource.OperatingSystem"/>.
		/// </summary>
		Task<UserLocation?> GetFromOperatingSystemAsync(CancellationToken ct = default);

		/// <summary>
		/// Estimates the location from the public IP address via a network lookup. City-level
		/// accuracy; returns <c>null</c> when offline or the lookup fails. The result's
		/// <see cref="UserLocation.Source"/> is <see cref="LocationSource.IpAddress"/>.
		/// </summary>
		Task<UserLocation?> GetFromIpAddressAsync(CancellationToken ct = default);

		/// <summary>
		/// Resolves the location with the OS method first and the IP method as a fallback. Returns
		/// <c>null</c> only if both fail. The result carries the source that actually succeeded.
		/// </summary>
		Task<UserLocation?> ResolveAsync(CancellationToken ct = default);
	}
}
