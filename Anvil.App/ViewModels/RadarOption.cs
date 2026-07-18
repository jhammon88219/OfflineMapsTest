using System.ComponentModel;
using Anvil.Models;

namespace Anvil.ViewModels
{
	/// <summary>
	/// One entry in the radar site selector: a display label plus the site it selects.
	/// A null <see cref="Site"/> is the "None" entry, which clears the radar layer.
	/// </summary>
	public record RadarOption(string Label, RadarSite? Site);

	/// <summary>
	/// One entry in the radar Tilt (elevation) selector. <see cref="Angle"/> is the elevation angle in
	/// degrees, or null for the BASE tilt — which is not merely "the lowest angle" but a distinct mode:
	/// it's the only tilt the near-real-time live frame exists at, and the only one whose fetch can use
	/// the cheap range-prefix path, so it keeps a null angle end-to-end (see
	/// <c>ILevel2RadarService.EnsureCachedAsync</c>).
	///
	/// The list comes from the VCP's designed elevation table, read out of the metadata that every
	/// cached tilt carries — so the choices are what the radar ACTUALLY scans in its current VCP, and
	/// they change when the radar switches VCP (clear-air scans fewer tilts than precip).
	/// </summary>
	public record RadarTiltOption(string Label, float? Angle);

	/// <summary>
	/// One entry in the radar Product (moment) selector — the C# mirror of the JS registry in
	/// <c>radar-products.js</c>. <see cref="Id"/> is the product id passed to <c>window.setRadarProduct</c>
	/// (must match the JS ids); <see cref="ShortLabel"/> is what the combo shows ("Ref"/"Vel"/…) with
	/// <see cref="Label"/> as the full name behind a tooltip; <see cref="IsLazy"/> marks a product built
	/// lazily (velocity — the only one that dealiases), so its frames aren't display-ready until built
	/// (drives the scrubber's "still loading" gate).
	///
	/// Observable (not a record) because <see cref="Ramp"/> is filled in LATER: the ramps are pushed from
	/// the WebView (radar-ramps.js) once it loads, and the combo draws each product's ramp from it — so the
	/// option has to raise a change when its ramp lands.
	/// </summary>
	public sealed class RadarProductOption : INotifyPropertyChanged
	{
		public RadarProductOption(string id, string label, string shortLabel, bool isLazy)
		{
			Id = id;
			Label = label;
			ShortLabel = shortLabel;
			IsLazy = isLazy;
		}

		/// <summary>Product id — must match the JS product id (radar-products.js).</summary>
		public string Id { get; }

		/// <summary>Full product name (the combo item's tooltip).</summary>
		public string Label { get; }

		/// <summary>Compact name shown in the combo ("Ref", "Vel", "CC", "KDP", "ZDR", "SW").</summary>
		public string ShortLabel { get; }

		/// <summary>Whether this product's geometry is built lazily (velocity only — it dealiases).</summary>
		public bool IsLazy { get; }

		private RadarRampInfo? _ramp;

		/// <summary>This product's color ramp, pushed from the WebView once radar-ramps.js loads (null
		/// until then). The combo renders it as the product's scale bar.</summary>
		public RadarRampInfo? Ramp
		{
			get => _ramp;
			set
			{
				if (ReferenceEquals(_ramp, value)) return;
				_ramp = value;
				PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Ramp)));
			}
		}

		public event PropertyChangedEventHandler? PropertyChanged;
	}
}
