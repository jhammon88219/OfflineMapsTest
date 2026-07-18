using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// Default <see cref="IDowEventProvider"/>. Lists the bundled <c>.dow.json</c> frames under
	/// <c>Assets/DowEvents/</c> (next to the app), exposing each as a <see cref="DowEvent"/> served
	/// from the <see cref="HostName"/> virtual host. No parsing of the (potentially multi-MB) frame
	/// happens here — the label is derived from the file name, and the WebView fetches + decodes the
	/// frame only when the user loads it.
	/// </summary>
	public sealed class DowEventProvider : IDowEventProvider
	{
		/// <summary>WebView2 virtual host the events folder is mapped to (see MainWindow).</summary>
		public const string HostName = "dowevents";

		/// <summary>The bundled events folder (alongside the app's other Assets).</summary>
		public static string EventsDirectory => Path.Combine(AppContext.BaseDirectory, "Assets", "DowEvents");

		public IReadOnlyList<DowEvent> GetEvents()
		{
			var dir = EventsDirectory;
			if (!Directory.Exists(dir))
			{
				return Array.Empty<DowEvent>();
			}

			return Directory.EnumerateFiles(dir, "*.dow.json")
				.OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
				.Select(f =>
				{
					var name = Path.GetFileName(f);
					return new DowEvent(name, LabelFor(name), $"https://{HostName}/{name}");
				})
				.ToList();
		}

		// Human label from the file name (the converter names files meaningfully, e.g.
		// "goshen_2009-06-05_DOW7.dow.json"). Strips the ".dow.json" suffix and tidies separators.
		// (Reading the frame's "event" field for a nicer label is a future refinement.)
		private static string LabelFor(string fileName)
		{
			var stem = fileName;
			if (stem.EndsWith(".dow.json", StringComparison.OrdinalIgnoreCase))
			{
				stem = stem[..^".dow.json".Length];
			}
			return stem.Replace('_', ' ').Trim();
		}
	}
}
