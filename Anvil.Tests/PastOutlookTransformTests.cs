using System.Text.Json;
using Anvil.Models;
using Anvil.Services;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// The IEM→renderer transform (<see cref="SpcOutlookColors.TryBuildProduct"/>) that lets historical
	/// outlooks reuse the live outlook render path: it must filter to the requested product's category,
	/// color each feature by threshold (the renderer reads <c>fill</c>/<c>stroke</c>), and mark significant
	/// areas <c>LABEL="SIGN"</c> so they hatch. Deterministic — no network.
	/// </summary>
	public class PastOutlookTransformTests
	{
		// A minimal IEM-shaped convective collection: categorical SLGT, tornado 5% + significant, hail 15%.
		private const string IemSample = """
		{
		  "type": "FeatureCollection",
		  "features": [
		    { "type": "Feature",
		      "properties": { "category": "CATEGORICAL", "threshold": "SLGT",
		        "issue": "2013-05-20T20:00:00Z", "expire": "2013-05-21T12:00:00Z" },
		      "geometry": { "type": "Polygon", "coordinates": [[[-98,35],[-97,35],[-97,36],[-98,36],[-98,35]]] } },
		    { "type": "Feature",
		      "properties": { "category": "TORNADO", "threshold": "0.05" },
		      "geometry": { "type": "Polygon", "coordinates": [[[-98,35],[-97,35],[-97,36],[-98,36],[-98,35]]] } },
		    { "type": "Feature",
		      "properties": { "category": "TORNADO", "threshold": "SIGN" },
		      "geometry": { "type": "Polygon", "coordinates": [[[-98,35],[-97,35],[-97,36],[-98,36],[-98,35]]] } },
		    { "type": "Feature",
		      "properties": { "category": "HAIL", "threshold": "0.15" },
		      "geometry": { "type": "Polygon", "coordinates": [[[-98,35],[-97,35],[-97,36],[-98,36],[-98,35]]] } }
		  ]
		}
		""";

		private static JsonElement Root() => JsonDocument.Parse(IemSample).RootElement;

		[Fact]
		public void Categorical_KeepsOnlyCategorical_WithSlgtFill()
		{
			var ok = SpcOutlookColors.TryBuildProduct(Root(), SpcOutlookType.Categorical, out var gj, out var times);
			Assert.True(ok);

			using var doc = JsonDocument.Parse(gj);
			var feats = doc.RootElement.GetProperty("features");
			Assert.Equal(1, feats.GetArrayLength()); // only the CATEGORICAL feature survives
			var props = feats[0].GetProperty("properties");
			Assert.Equal("#FFE066", props.GetProperty("fill").GetString()); // SPC Slight yellow
			Assert.Equal("", props.GetProperty("LABEL").GetString());       // categorical is never hatched
			Assert.NotNull(times);
		}

		[Fact]
		public void Tornado_KeepsProbAndSignificant_AndLabelsSign()
		{
			var ok = SpcOutlookColors.TryBuildProduct(Root(), SpcOutlookType.Tornado, out var gj, out _);
			Assert.True(ok);

			using var doc = JsonDocument.Parse(gj);
			var feats = doc.RootElement.GetProperty("features");
			Assert.Equal(2, feats.GetArrayLength()); // 5% + SIGN

			string? signLabel = null;
			foreach (var f in feats.EnumerateArray())
			{
				var p = f.GetProperty("properties");
				if (p.GetProperty("threshold").GetString() == "SIGN")
				{
					signLabel = p.GetProperty("LABEL").GetString();
				}
			}
			Assert.Equal("SIGN", signLabel); // significant area is hatched by the renderer
		}

		[Fact]
		public void ProbabilisticCombined_MapsAnySevere_WithProbRamp()
		{
			// Days 2-3 carry a single "ANY SEVERE" combined probabilistic (not separate tor/wind/hail).
			const string day3 = """
			{ "type": "FeatureCollection", "features": [
			  { "type": "Feature", "properties": { "category": "ANY SEVERE", "threshold": "0.15" },
			    "geometry": { "type": "Polygon", "coordinates": [[[-98,35],[-97,35],[-97,36],[-98,36],[-98,35]]] } },
			  { "type": "Feature", "properties": { "category": "CATEGORICAL", "threshold": "SLGT" },
			    "geometry": { "type": "Polygon", "coordinates": [[[-98,35],[-97,35],[-97,36],[-98,36],[-98,35]]] } } ] }
			""";
			var root = JsonDocument.Parse(day3).RootElement;

			var ok = SpcOutlookColors.TryBuildProduct(root, SpcOutlookType.ProbabilisticCombined, out var gj, out _);
			Assert.True(ok);
			using var doc = JsonDocument.Parse(gj);
			var feats = doc.RootElement.GetProperty("features");
			Assert.Equal(1, feats.GetArrayLength()); // only ANY SEVERE, not the categorical
			// 15% uses the probabilistic ramp (red), NOT a categorical color.
			Assert.Equal("#FF0000", feats[0].GetProperty("properties").GetProperty("fill").GetString());
		}

		[Fact]
		public void MissingProduct_ReturnsFalse()
		{
			// No WIND features in the sample → nothing to draw.
			var ok = SpcOutlookColors.TryBuildProduct(Root(), SpcOutlookType.Wind, out var gj, out _);
			Assert.False(ok);
			Assert.Equal("", gj);
		}
	}
}
