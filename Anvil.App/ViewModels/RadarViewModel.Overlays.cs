using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Anvil.Models;
using Anvil.Services;

namespace Anvil.ViewModels
{
	// RadarViewModel (partial): the color-scale legend + the inspector (read value under cursor).
	public sealed partial class RadarViewModel
	{
		// ── Color-scale legend. Fed by the WebView pushing the active product's ramp (from
		//    radar-ramps.js, the single source of truth) — never hard-coded here. Updates whenever the
		//    product changes; the Color Scale tool window renders the bar from these exact stops. ──
		private RadarRampInfo? _currentRamp;

		/// <summary>The active product's color ramp (or null until the first push). Drives the legend.</summary>
		public RadarRampInfo? CurrentRamp => _currentRamp;

		/// <summary>Whether to show the color-scale legend (a product ramp is known + a loop is active).</summary>
		public bool HasColorScale => _currentRamp is not null && HasRadarDisplay;

		/// <summary>Legend heading, e.g. "Reflectivity (dBZ)".</summary>
		public string RampTitle => _currentRamp is { } r ? $"{r.Label} ({r.Unit})" : string.Empty;

		/// <summary>Legend tick labels at the low / mid / high ends of the scale.</summary>
		public string RampMinText => _currentRamp is { } r ? FormatRampValue(r.Min) : string.Empty;
		public string RampMidText => _currentRamp is { } r ? FormatRampValue((r.Min + r.Max) / 2) : string.Empty;
		public string RampMaxText => _currentRamp is { } r ? FormatRampValue(r.Max) : string.Empty;

		private static string FormatRampValue(double v) =>
			v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

		/// <summary>Called from the view when the WebView pushes the active product's ramp.</summary>
		public void SetColorScale(RadarRampInfo? ramp)
		{
			_currentRamp = ramp;
			OnPropertyChanged(nameof(CurrentRamp));
			OnPropertyChanged(nameof(HasColorScale));
			OnPropertyChanged(nameof(RampTitle));
			OnPropertyChanged(nameof(RampMinText));
			OnPropertyChanged(nameof(RampMidText));
			OnPropertyChanged(nameof(RampMaxText));
			OnPropertyChanged(nameof(IsInspectMarkerVisible));
		}

		// ── Inspector ("read the value under the cursor", RadarScope-style). The toggle drives the
		//    WebView's inspect mode; the WebView pushes the value under the pointer back (SetInspectValue),
		//    which positions a live marker on the Color Scale bar. The value tooltip itself is drawn in
		//    the WebView next to the cursor (instant, no host round-trip per mouse move). ──
		private bool _isInspecting;
		private bool _hasInspectValue;
		private double _inspectFraction;
		private string _inspectValueText = string.Empty;

		/// <summary>Whether inspect mode is engaged (Radar Loop tool window toggle).</summary>
		public bool IsInspecting
		{
			get => _isInspecting;
			set
			{
				if (_isInspecting == value)
				{
					return;
				}

				_isInspecting = value;
				OnPropertyChanged();
				if (!value)
				{
					SetInspectValue(null); // clear the marker when leaving inspect
				}
				OnPropertyChanged(nameof(IsInspectMarkerVisible));

				if (_isMapReady)
				{
					_ = _mapService.SetRadarInspectAsync(value);
				}
			}
		}

		/// <summary>Whether the live inspect marker should be shown on the color-scale bar.</summary>
		public bool IsInspectMarkerVisible => _isInspecting && _hasInspectValue && HasColorScale;

		/// <summary>Position (0-1) of the inspected value along the active ramp.</summary>
		public double InspectFraction => _inspectFraction;

		/// <summary>The inspected value formatted with the product unit (e.g. "47.5 dBZ").</summary>
		public string InspectValueText => _inspectValueText;

		/// <summary>Called from the view when the WebView pushes the value under the cursor (null = none).</summary>
		public void SetInspectValue(double? value)
		{
			if (value is double v && _currentRamp is { } r)
			{
				double span = r.Max - r.Min;
				if (span <= 0)
				{
					span = 1;
				}

				_inspectFraction = Math.Clamp((v - r.Min) / span, 0, 1);
				// Velocity (m/s) reads in familiar speed units (mph · km/h) to match the on-map inspector
				// tooltip; other products keep their native unit (dBZ / unitless CC).
				_inspectValueText = r.Unit == "m/s"
					? $"{v * 2.23694:0} mph · {v * 3.6:0} km/h"
					: v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) +
						(string.IsNullOrEmpty(r.Unit) ? string.Empty : " " + r.Unit);
				_hasInspectValue = true;
			}
			else
			{
				_hasInspectValue = false;
			}

			OnPropertyChanged(nameof(InspectFraction));
			OnPropertyChanged(nameof(InspectValueText));
			OnPropertyChanged(nameof(IsInspectMarkerVisible));
		}
	}
}
