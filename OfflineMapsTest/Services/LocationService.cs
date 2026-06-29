using System;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OfflineMapsTest.Models;
using Windows.Devices.Geolocation;

namespace OfflineMapsTest.Services
{
	/// <summary>
	/// Default <see cref="ILocationService"/>. The OS method uses
	/// <see cref="Windows.Devices.Geolocation.Geolocator"/> (requires the <c>location</c> device
	/// capability in the manifest + user consent); the IP method does a single HTTPS GET to a free
	/// IP-geolocation endpoint. Both swallow failures and return <c>null</c> so the caller can fall
	/// back. Mirrors the no-DI, own-HttpClient style of the other services.
	/// </summary>
	public sealed class LocationService : ILocationService
	{
		// Free, key-less, HTTPS IP-geolocation lookup. Returns { success, latitude, longitude,
		// city, region, country }. Kept here (not in an editable catalog) — it's a single fallback
		// endpoint, swap-in-place if it ever goes away.
		private const string IpLookupUrl = "https://ipwho.is/";

		private readonly HttpClient _http;

		public LocationService()
		{
			_http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
			_http.DefaultRequestHeaders.UserAgent.ParseAdd("OfflineMapsTest/1.0");
		}

		public async Task<UserLocation?> GetFromOperatingSystemAsync(CancellationToken ct = default)
		{
			try
			{
				// Prompts the user once (per package) and reflects the system location toggle.
				var access = await Geolocator.RequestAccessAsync();
				if (access != GeolocationAccessStatus.Allowed)
				{
					return null;
				}

				var geolocator = new Geolocator { DesiredAccuracyInMeters = 100 };
				var position = await geolocator.GetGeopositionAsync().AsTask(ct);
				var coord = position.Coordinate;
				var point = coord.Point.Position;

				return new UserLocation(
					point.Latitude,
					point.Longitude,
					LocationSource.OperatingSystem,
					coord.Accuracy,
					"Device location");
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch
			{
				// No fix, consent revoked mid-call, location services off, etc. — fall back to IP.
				return null;
			}
		}

		public async Task<UserLocation?> GetFromIpAddressAsync(CancellationToken ct = default)
		{
			try
			{
				using var response = await _http.GetAsync(IpLookupUrl, ct);
				response.EnsureSuccessStatusCode();
				var json = await response.Content.ReadAsStringAsync(ct);

				using var doc = JsonDocument.Parse(json);
				var root = doc.RootElement;

				// ipwho.is reports failures with "success": false rather than an HTTP error.
				if (root.TryGetProperty("success", out var success) &&
					success.ValueKind == JsonValueKind.False)
				{
					return null;
				}

				if (!TryGetCoordinate(root, "latitude", out var lat) ||
					!TryGetCoordinate(root, "longitude", out var lon))
				{
					return null;
				}

				return new UserLocation(lat, lon, LocationSource.IpAddress, null, DescribePlace(root));
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch
			{
				// Offline or the endpoint is unreachable / changed shape — no IP location.
				return null;
			}
		}

		public async Task<UserLocation?> ResolveAsync(CancellationToken ct = default) =>
			await GetFromOperatingSystemAsync(ct) ?? await GetFromIpAddressAsync(ct);

		// ipwho.is returns latitude/longitude as JSON numbers, but tolerate a stringified number too.
		private static bool TryGetCoordinate(JsonElement root, string name, out double value)
		{
			value = 0;
			if (!root.TryGetProperty(name, out var el))
			{
				return false;
			}
			if (el.ValueKind == JsonValueKind.Number)
			{
				value = el.GetDouble();
				return true;
			}
			return el.ValueKind == JsonValueKind.String &&
				double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
		}

		// "City, Region" (whichever are present) for the marker tooltip / status line.
		private static string? DescribePlace(JsonElement root)
		{
			var city = GetString(root, "city");
			var region = GetString(root, "region");
			if (!string.IsNullOrEmpty(city) && !string.IsNullOrEmpty(region))
			{
				return $"{city}, {region}";
			}
			return city ?? region ?? GetString(root, "country");
		}

		private static string? GetString(JsonElement root, string name) =>
			root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
				? el.GetString()
				: null;
	}
}
