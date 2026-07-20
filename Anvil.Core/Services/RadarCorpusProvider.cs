using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// Default <see cref="IRadarCorpusProvider"/>. Loads the velocity-dealias validation corpus from the
	/// bundled <c>Assets/radar-corpus.json</c> manifest (mirroring how <see cref="RadarSiteProvider"/>
	/// reads its bundled data files). Parsed once and cached. Fully offline — the corpus is fixed by
	/// design, so the only variable between runs is the dealias code.
	///
	/// The referenced <c>.V06</c> volumes live under <c>Assets/RadarCorpus/</c> (bundled) and are served
	/// to the WebView through the <see cref="CorpusHostName"/> virtual host, which the app maps to that
	/// folder. A manifest entry may instead give an absolute path (the local-folder override) to score a
	/// volume kept out of the repo.
	/// </summary>
	public sealed class RadarCorpusProvider : IRadarCorpusProvider
	{
		/// <summary>The WebView2 virtual host the bundled corpus folder is mapped to (see MainWindow's host
		/// table). The JS scorer fetches volumes from <c>https://radarcorpus/&lt;file&gt;</c>.</summary>
		public const string CorpusHostName = "radarcorpus";

		/// <summary>The bundled corpus folder, alongside the app's other Assets (same convention as
		/// <see cref="DowEventProvider.EventsDirectory"/>).</summary>
		public static string CorpusDirectory => Path.Combine(AppContext.BaseDirectory, "Assets", "RadarCorpus");

		private static readonly JsonSerializerOptions JsonOptions = new()
		{
			PropertyNameCaseInsensitive = true,
		};

		private readonly Lazy<IReadOnlyList<RadarCorpusEntry>> _entries = new(LoadEntries);

		public IReadOnlyList<RadarCorpusEntry> GetEntries() => _entries.Value;

		private static IReadOnlyList<RadarCorpusEntry> LoadEntries()
		{
			try
			{
				var path = Path.Combine(AppContext.BaseDirectory, "Assets", "radar-corpus.json");
				var manifest = JsonSerializer.Deserialize<ManifestDto>(File.ReadAllText(path), JsonOptions);
				var volumes = manifest?.Volumes;
				if (volumes is { Count: > 0 })
				{
					return volumes
						.Where(v => !string.IsNullOrWhiteSpace(v.Id) && !string.IsNullOrWhiteSpace(v.File))
						.Select(v => new RadarCorpusEntry(
							v.Id!, v.File!, v.Name ?? v.Id!, v.Lat, v.Lon, v.ExpectedPct, v.TolerancePct))
						.ToList();
				}
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"[radar] radar-corpus.json load failed: {ex.Message}");
			}

			return Array.Empty<RadarCorpusEntry>();
		}

		private sealed class ManifestDto
		{
			public List<VolumeDto>? Volumes { get; set; }
		}

		private sealed class VolumeDto
		{
			public string? Id { get; set; }
			public string? File { get; set; }
			public string? Name { get; set; }
			public double Lat { get; set; }
			public double Lon { get; set; }
			public double ExpectedPct { get; set; }
			public double TolerancePct { get; set; }
		}
	}
}
