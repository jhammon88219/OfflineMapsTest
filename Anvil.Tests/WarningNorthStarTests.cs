using System;
using System.Linq;
using Anvil.Services;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// The CAP→render transform (<see cref="WarningService.TryTransformCap"/>) and the north-star
	/// reconciliation (<see cref="WarningsHealthLog.Reconcile"/>) — the pieces that let the app render
	/// from the authoritative api.weather.gov feed and continuously check "what NWS has" against "what we
	/// show". Deterministic, no network.
	/// </summary>
	public class WarningNorthStarTests
	{
		// A minimal api.weather.gov active-alerts geo+json body: one tornado, one severe, one unrelated
		// event (flood — must be ignored), and one warning with null geometry (can't render — skipped).
		private const string CapSample = """
		{
		  "type": "FeatureCollection",
		  "features": [
		    { "type": "Feature",
		      "geometry": { "type": "Polygon", "coordinates": [[[-98,35],[-97,35],[-97,36],[-98,36],[-98,35]]] },
		      "properties": { "id": "urn:tor:1", "event": "Tornado Warning", "expires": "2026-07-21T18:00:00-05:00" } },
		    { "type": "Feature",
		      "geometry": { "type": "Polygon", "coordinates": [[[-96,34],[-95,34],[-95,35],[-96,35],[-96,34]]] },
		      "properties": { "id": "urn:svr:1", "event": "Severe Thunderstorm Warning", "expires": "2026-07-21T18:30:00-05:00" } },
		    { "type": "Feature",
		      "geometry": { "type": "Polygon", "coordinates": [[[-90,30],[-89,30],[-89,31],[-90,31],[-90,30]]] },
		      "properties": { "id": "urn:ffw:1", "event": "Flood Warning", "expires": "2026-07-21T20:00:00-05:00" } },
		    { "type": "Feature",
		      "geometry": null,
		      "properties": { "id": "urn:tor:2", "event": "Tornado Warning", "expires": "2026-07-21T18:00:00-05:00" } }
		  ]
		}
		""";

		[Fact]
		public void TransformCap_KeepsOnlyRenderableTornadoAndSevereWarnings()
		{
			Assert.True(WarningService.TryTransformCap(CapSample, out var features, out var ids));

			// Flood (wrong event) and the null-geometry tornado are dropped; tornado + severe remain.
			Assert.Equal(new[] { "urn:svr:1", "urn:tor:1" }, ids.OrderBy(x => x));
			Assert.Equal(2, features.Count);

			// Each rendered feature carries the phenom the map colors by, plus the merge key + geometry.
			var byId = features.ToDictionary(f => f!["properties"]!["cap_id"]!.GetValue<string>());
			Assert.Equal("TO", byId["urn:tor:1"]!["properties"]!["phenom"]!.GetValue<string>());
			Assert.Equal("SV", byId["urn:svr:1"]!["properties"]!["phenom"]!.GetValue<string>());
			Assert.NotNull(byId["urn:tor:1"]!["geometry"]);
		}

		[Fact]
		public void TransformCap_ReturnsFalse_OnNonFeatureCollection()
		{
			Assert.False(WarningService.TryTransformCap("""{"status":404,"detail":"error"}""", out _, out _));
		}

		[Fact]
		public void TransformCap_FeedsTheMergeAndRendersTheCapSet()
		{
			var svc = new WarningService();
			WarningService.TryTransformCap(CapSample, out var features, out _);

			// CAP is authoritative + complete → its own count is the authoritative count.
			var count = svc.ApplyFetch(features, features.Count, new DateTimeOffset(2026, 7, 21, 22, 0, 0, TimeSpan.Zero));

			Assert.Equal(2, count);
			Assert.Equal(new[] { "urn:svr:1", "urn:tor:1" }, svc.ActiveIds.OrderBy(x => x));
		}

		private static readonly DateTimeOffset T = new(2026, 7, 21, 22, 0, 0, TimeSpan.Zero);

		[Fact]
		public void Reconcile_OK_WhenDisplayMatchesTheCapNorthStar()
		{
			var h = WarningsHealthLog.Reconcile(T,
				displayed: new[] { "A", "B" }, primary: new[] { "A", "B" }, crossCheck: new[] { "A", "B" },
				mergeClassification: "complete(dropped=0)");

			Assert.Equal("OK", h.Verdict);
			Assert.Empty(h.MissingFromDisplay);
			Assert.Empty(h.ExtraInDisplay);
			Assert.Equal(2, h.CrossCheckCount);
		}

		[Fact]
		public void Reconcile_FlagsAWarningActivePerCapButNotShown() // the dangerous case
		{
			var h = WarningsHealthLog.Reconcile(T,
				displayed: new[] { "A" }, primary: new[] { "A", "B" }, crossCheck: null,
				mergeClassification: "partial-held");

			Assert.Equal("DISPLAY_DIVERGED", h.Verdict);
			Assert.Equal(new[] { "B" }, h.MissingFromDisplay);
			Assert.Empty(h.ExtraInDisplay);
		}

		[Fact]
		public void Reconcile_CapturesSourceDivergence_WhenWwaLagsBehindCap()
		{
			// We render from CAP {A,B,C}; WWA cross-check only has {A} (its wrong-empty/lagging episode).
			var h = WarningsHealthLog.Reconcile(T,
				displayed: new[] { "A", "B", "C" }, primary: new[] { "A", "B", "C" }, crossCheck: new[] { "A" },
				mergeClassification: "complete(dropped=0)");

			Assert.Equal("OK", h.Verdict);                              // display matches the authoritative source
			Assert.Equal(new[] { "B", "C" }, h.OnlyPrimary.OrderBy(x => x)); // …but WWA is missing 2 → logged
			Assert.Empty(h.OnlyCrossCheck);
		}

		[Fact]
		public void Reconcile_MarksCrossCheckUnavailable_AsNegativeCount()
		{
			var h = WarningsHealthLog.Reconcile(T,
				displayed: new[] { "A" }, primary: new[] { "A" }, crossCheck: null,
				mergeClassification: "complete(dropped=0)");

			Assert.Equal(-1, h.CrossCheckCount);
		}
	}
}
