using System.Collections.Generic;

namespace OfflineMapsTest.Models
{
	/// <summary>One color-ramp stop: a value and its [r,g,b] color (0-255).</summary>
	public sealed class RadarRampStop
	{
		public double V { get; set; }
		public int[] Color { get; set; } = new int[3];
	}

	/// <summary>
	/// A radar product's color ramp, pushed from the WebView's <c>radar-ramps.js</c> — the SINGLE
	/// source of truth that also colors the gates — so the color-scale legend is generated from the
	/// exact same data the renderer uses (never a hand-maintained copy). <see cref="Interpolate"/>
	/// false = discrete NWS bands (reflectivity), true = smooth gradient (velocity, CC).
	/// </summary>
	public sealed class RadarRampInfo
	{
		public string Id { get; set; } = string.Empty;
		public string Label { get; set; } = string.Empty;
		public string Unit { get; set; } = string.Empty;
		public double Min { get; set; }
		public double Max { get; set; }
		public bool Interpolate { get; set; }
		public List<RadarRampStop> Stops { get; set; } = new();
	}
}
