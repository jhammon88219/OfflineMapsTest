using System;
using System.IO;
using Windows.Storage;

namespace Anvil.Services
{
	/// <summary>
	/// Default <see cref="ISettingsService"/>, backed by the packaged app's
	/// <see cref="ApplicationData.Current"/> <c>LocalSettings</c> key/value store.
	/// </summary>
	public sealed class SettingsService : ISettingsService
	{
		private const string MapDataFolderKey = "MapDataFolder";

		public string MapDataFileName => "usa_full.pmtiles";

		public string MapDataFolder
		{
			get
			{
				var saved = ApplicationData.Current.LocalSettings.Values[MapDataFolderKey] as string;
				return string.IsNullOrWhiteSpace(saved) ? ResolveDefaultFolder() : saved!;
			}
			set => ApplicationData.Current.LocalSettings.Values[MapDataFolderKey] = value;
		}

		public bool MapDataFilePresent(string? folder = null)
		{
			var f = folder ?? MapDataFolder;
			try
			{
				return !string.IsNullOrWhiteSpace(f) && File.Exists(Path.Combine(f, MapDataFileName));
			}
			catch
			{
				return false;
			}
		}

		// Runtime-resolved default — NEVER a hardcoded user path (which would leak the username into
		// source and only work on one machine). Picks the first candidate folder that actually holds
		// the basemap file (so an OneDrive-redirected Desktop is handled), else falls back to Desktop.
		private string ResolveDefaultFolder()
		{
			var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
			var candidates = new[]
			{
				desktop,
				string.IsNullOrEmpty(profile) ? string.Empty : Path.Combine(profile, "OneDrive", "Desktop"),
				string.IsNullOrEmpty(profile) ? string.Empty : Path.Combine(profile, "Desktop"),
			};

			foreach (var c in candidates)
			{
				try
				{
					if (!string.IsNullOrEmpty(c) && File.Exists(Path.Combine(c, MapDataFileName)))
					{
						return c;
					}
				}
				catch
				{
					// Ignore an inaccessible candidate and try the next.
				}
			}

			return string.IsNullOrEmpty(desktop) ? profile : desktop;
		}
	}
}
