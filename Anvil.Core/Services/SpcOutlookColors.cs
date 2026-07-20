using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// Turns an IEM archive SPC-outlook GeoJSON (which carries a bare <c>category</c>/<c>threshold</c> but
	/// NO colors) into the schema the map renderer (<c>outlook.js</c>) expects: per-feature <c>fill</c> /
	/// <c>stroke</c> hex + a <c>LABEL</c> ("SIGN" ⇒ hatched, else solid fill) + <c>ISSUE_ISO</c>/
	/// <c>EXPIRE_ISO</c> for the times readout. This is what lets historical outlooks reuse the existing
	/// live-outlook render path with zero JS change.
	///
	/// Colors follow SPC's published outlook legend (categorical / shared probabilistic / fire). The
	/// renderer coalesces an unknown threshold to gray, so a stray value degrades gracefully.
	/// </summary>
	public static class SpcOutlookColors
	{
		// ── SPC categorical (General Thunder → High) ──
		private static readonly Dictionary<string, (string Fill, string Stroke)> Categorical = new(StringComparer.OrdinalIgnoreCase)
		{
			["TSTM"] = ("#C1E9C1", "#55A555"),
			["MRGL"] = ("#66A366", "#3C7A3C"),
			["SLGT"] = ("#FFE066", "#D6B000"),
			["ENH"]  = ("#FFA366", "#E07711"),
			["MDT"]  = ("#E6635C", "#C0392B"),
			["HIGH"] = ("#EE99EE", "#CC33CC"),
		};

		// ── SPC fire weather (by threshold code; dry-thunderstorm areas included) ──
		private static readonly Dictionary<string, (string Fill, string Stroke)> Fire = new(StringComparer.OrdinalIgnoreCase)
		{
			["ELEV"] = ("#FFCC33", "#D6A017"),
			["CRIT"] = ("#FF6600", "#CC4E00"),
			["EXTM"] = ("#FF00FF", "#CC00CC"),
			["IDRT"] = ("#CC9966", "#A5794A"), // isolated dry thunderstorm
			["SDRT"] = ("#996633", "#73491F"), // scattered dry thunderstorm
		};

		// Shared probabilistic ramp for Tornado / Wind / Hail (percent → color). Ordered ascending.
		private static readonly (double P, string Fill, string Stroke)[] Prob =
		{
			(0.02, "#008B00", "#006400"),
			(0.05, "#8B4726", "#5E2F19"),
			(0.10, "#FFC800", "#D6A700"),
			(0.15, "#FF0000", "#CC0000"),
			(0.30, "#FF00FF", "#CC00CC"),
			(0.45, "#912CEE", "#6E1FB5"),
			(0.60, "#104E8B", "#0B385F"),
		};

		private const string SigStroke = "#000000";

		/// <summary>
		/// Builds one product's renderer-ready GeoJSON from an IEM outlook collection, keeping only the
		/// features for <paramref name="type"/> and coloring each. Returns false (with an empty document)
		/// when the issuance carried nothing for this product.
		/// </summary>
		public static bool TryBuildProduct(JsonElement iemRoot, SpcOutlookType type,
			out string geoJson, out SpcOutlookTimes? times)
		{
			geoJson = string.Empty;
			times = null;

			if (iemRoot.ValueKind != JsonValueKind.Object ||
				!iemRoot.TryGetProperty("features", out var features) ||
				features.ValueKind != JsonValueKind.Array)
			{
				return false;
			}

			var wantCategory = CategoryFor(type); // null ⇒ fire (take every feature)
			var buffer = new System.Buffers.ArrayBufferWriter<byte>();
			using var writer = new Utf8JsonWriter(buffer);
			var written = 0;
			DateTimeOffset? issue = null, product = null, expire = null;

			writer.WriteStartObject();
			writer.WriteString("type", "FeatureCollection");
			writer.WritePropertyName("features");
			writer.WriteStartArray();

			foreach (var f in features.EnumerateArray())
			{
				if (!f.TryGetProperty("properties", out var props) ||
					!f.TryGetProperty("geometry", out var geom))
				{
					continue;
				}

				var category = GetStr(props, "category");
				if (wantCategory is not null &&
					!string.Equals(category, wantCategory, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				var threshold = GetStr(props, "threshold") ?? string.Empty;
				var (fill, stroke, label) = Style(type, category, threshold);

				// First feature's times represent the issuance (all share them).
				issue ??= ParseIso(GetStr(props, "issue"));
				product ??= ParseIso(GetStr(props, "product_issue"));
				expire ??= ParseIso(GetStr(props, "expire"));

				writer.WriteStartObject();
				writer.WriteString("type", "Feature");
				writer.WritePropertyName("properties");
				writer.WriteStartObject();
				writer.WriteString("fill", fill);
				writer.WriteString("stroke", stroke);
				writer.WriteString("LABEL", label);
				writer.WriteString("threshold", threshold);
				if (issue is { } iss) writer.WriteString("ISSUE_ISO", iss.ToString("O"));
				if (expire is { } exp) writer.WriteString("EXPIRE_ISO", exp.ToString("O"));
				writer.WriteEndObject();
				writer.WritePropertyName("geometry");
				geom.WriteTo(writer);
				writer.WriteEndObject();
				written++;
			}

			writer.WriteEndArray();
			writer.WriteEndObject();
			writer.Flush();

			if (written == 0)
			{
				return false;
			}

			geoJson = System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
			// Issued = the product-issuance time if present (else the valid start); valid window = issue→expire.
			times = new SpcOutlookTimes(product ?? issue, issue, expire);
			return true;
		}

		// The IEM `category` value each product type maps to; null means "fire" (take all F-collection features).
		private static string? CategoryFor(SpcOutlookType type) => type switch
		{
			SpcOutlookType.Categorical => "CATEGORICAL",
			SpcOutlookType.Tornado => "TORNADO",
			SpcOutlookType.Wind => "WIND",
			SpcOutlookType.Hail => "HAIL",
			SpcOutlookType.ProbabilisticCombined => "ANY SEVERE", // Day 2-3 combined probabilistic
			_ => null, // FireWeather / ExtendedFireWeather
		};

		// Resolves the fill/stroke/LABEL for one feature. Significant ("SIGN") always hatches (LABEL=SIGN).
		private static (string Fill, string Stroke, string Label) Style(SpcOutlookType type, string? category, string threshold)
		{
			if (string.Equals(threshold, "SIGN", StringComparison.OrdinalIgnoreCase))
			{
				return ("#000000", SigStroke, "SIGN"); // hatched by the renderer; fill is unused under the pattern
			}

			var isFire = type is SpcOutlookType.FireWeather or SpcOutlookType.ExtendedFireWeather;
			if (isFire && Fire.TryGetValue(threshold, out var fc))
			{
				return (fc.Fill, fc.Stroke, string.Empty);
			}

			var isCategorical = type is SpcOutlookType.Categorical
				|| string.Equals(category, "CATEGORICAL", StringComparison.OrdinalIgnoreCase);
			if (isCategorical && Categorical.TryGetValue(threshold, out var cc))
			{
				return (cc.Fill, cc.Stroke, string.Empty);
			}

			// Probabilistic tornado/wind/hail AND the Day 2-3 combined "ANY SEVERE": threshold is a
			// fraction like "0.05".
			if (double.TryParse(threshold, NumberStyles.Float, CultureInfo.InvariantCulture, out var p))
			{
				var (fill, stroke) = ProbColor(p);
				return (fill, stroke, string.Empty);
			}

			return ("#888888", "#555555", string.Empty); // unknown ⇒ neutral (matches the renderer's coalesce)
		}

		private static (string Fill, string Stroke) ProbColor(double p)
		{
			var best = Prob[0];
			foreach (var band in Prob)
			{
				if (p + 1e-6 >= band.P) best = band; // highest band at or below p
			}
			return (best.Fill, best.Stroke);
		}

		private static string? GetStr(JsonElement obj, string name) =>
			obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

		private static DateTimeOffset? ParseIso(string? s) =>
			s is not null && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
				DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt)
				? dt
				: null;
	}
}
