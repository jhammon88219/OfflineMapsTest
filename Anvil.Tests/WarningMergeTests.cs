using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Anvil.Services;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// The warning-set robustness core (<see cref="WarningService.ApplyFetch"/>): the NWS origin
	/// intermittently returns a spurious EMPTY set or a PARTIAL one, so trusting each fetch and replacing
	/// the display wholesale showed "1 of 4" and let a single bad poll blank the map. ApplyFetch instead
	/// MERGES fetches into a keyed, expiration-aware active set and only drops a warning when it's really
	/// gone (an authoritative complete snapshot omits it, confirmed all-clear, or past expiration). These
	/// tests reproduce the exact failure modes and assert the set survives them. HTTP-free, deterministic.
	/// </summary>
	public class WarningMergeTests
	{
		private static readonly DateTimeOffset T0 = new(2026, 7, 21, 20, 0, 0, TimeSpan.Zero);

		// A warning feature keyed by cap_id, expiring at the given instant.
		private static JsonNode Warning(string id, DateTimeOffset expires, string phenom = "TO")
		{
			return new JsonObject
			{
				["type"] = "Feature",
				["properties"] = new JsonObject
				{
					["cap_id"] = id,
					["phenom"] = phenom,
					["expiration"] = expires.ToString("o"),
				},
				["geometry"] = new JsonObject { ["type"] = "Polygon", ["coordinates"] = new JsonArray() },
			};
		}

		private static List<JsonNode> Set(params JsonNode[] features) => features.ToList();
		private static readonly List<JsonNode> Empty = new();

		// Four active warnings, all expiring well beyond the test window.
		private static List<JsonNode> FourWarnings() => Set(
			Warning("A", T0.AddMinutes(30)), Warning("B", T0.AddMinutes(30)),
			Warning("C", T0.AddMinutes(30)), Warning("D", T0.AddMinutes(30)));

		[Fact]
		public void PartialFetch_DoesNotDropTheOmittedWarnings() // the "showed 1 of 4" bug
		{
			var svc = new WarningService();
			Assert.Equal(4, svc.ApplyFetch(FourWarnings(), authoritative: 4, T0));

			// Next poll returns only 1 feature, but the count still says 4 → partial, NOT authoritative.
			var partial = svc.ApplyFetch(Set(Warning("A", T0.AddMinutes(30))), authoritative: 4, T0.AddSeconds(15));

			Assert.Equal(4, partial);
			Assert.Equal(new[] { "A", "B", "C", "D" }, svc.ActiveIds.OrderBy(x => x));
		}

		[Fact]
		public void SpuriousEmptyGeoJson_DoesNotBlank_WhenCountSaysNonZero() // the "disappeared" bug
		{
			var svc = new WarningService();
			svc.ApplyFetch(FourWarnings(), authoritative: 4, T0);

			// GeoJSON came back empty but the count endpoint still reports 4 → keep the set.
			var after = svc.ApplyFetch(Empty, authoritative: 4, T0.AddSeconds(15));

			Assert.Equal(4, after);
		}

		[Fact]
		public void EmptyGeoJson_WithCountUnknown_KeepsTheSet()
		{
			var svc = new WarningService();
			svc.ApplyFetch(FourWarnings(), authoritative: 4, T0);

			// Both the GeoJSON empty AND the count check failed (-1 = unknown) → must not drop anything.
			var after = svc.ApplyFetch(Empty, authoritative: -1, T0.AddSeconds(15));

			Assert.Equal(4, after);
		}

		[Fact]
		public void SingleBothEmptyBlink_DoesNotClear_ButConfirmedAllClearDoes()
		{
			var svc = new WarningService();
			svc.ApplyFetch(FourWarnings(), authoritative: 4, T0);

			// One cycle where both endpoints say zero — a possible double-blink, so hold the set.
			Assert.Equal(4, svc.ApplyFetch(Empty, authoritative: 0, T0.AddSeconds(15)));

			// A SECOND consecutive both-empty cycle confirms it — now clear.
			Assert.Equal(0, svc.ApplyFetch(Empty, authoritative: 0, T0.AddSeconds(30)));
		}

		[Fact]
		public void NonEmptyCycle_ResetsTheEmptyConfirmationCounter()
		{
			var svc = new WarningService();
			svc.ApplyFetch(FourWarnings(), authoritative: 4, T0);

			Assert.Equal(4, svc.ApplyFetch(Empty, authoritative: 0, T0.AddSeconds(15)));   // 1st blink
			Assert.Equal(4, svc.ApplyFetch(FourWarnings(), authoritative: 4, T0.AddSeconds(30))); // real data resets
			Assert.Equal(4, svc.ApplyFetch(Empty, authoritative: 0, T0.AddSeconds(45)));   // blink again = 1st, not 2nd

			Assert.Equal(4, svc.ActiveIds.Count);
		}

		[Fact]
		public void CompleteSnapshot_DropsACancelledWarningPromptly()
		{
			var svc = new WarningService();
			svc.ApplyFetch(FourWarnings(), authoritative: 4, T0);

			// D was cancelled: the server now lists 3 and the count agrees (3) → authoritative, drop D.
			var after = svc.ApplyFetch(
				Set(Warning("A", T0.AddMinutes(30)), Warning("B", T0.AddMinutes(30)), Warning("C", T0.AddMinutes(30))),
				authoritative: 3, T0.AddSeconds(15));

			Assert.Equal(3, after);
			Assert.DoesNotContain("D", svc.ActiveIds);
		}

		[Fact]
		public void ExpiredWarning_IsPrunedEvenIfTheServerKeepsMissingIt()
		{
			var svc = new WarningService();
			// One warning expiring at T0+10min.
			svc.ApplyFetch(Set(Warning("A", T0.AddMinutes(10))), authoritative: 1, T0);

			// 13 min later, only ever seeing partial/unknown fetches: past expiration (+2 min grace) → pruned.
			var after = svc.ApplyFetch(Empty, authoritative: -1, T0.AddMinutes(13));

			Assert.Equal(0, after);
		}

		[Fact]
		public void ExpirationGrace_KeepsAWarningJustPastItsStatedExpiration()
		{
			var svc = new WarningService();
			svc.ApplyFetch(Set(Warning("A", T0.AddMinutes(10))), authoritative: 1, T0);

			// 11 min later: past expiration but within the 2-min skew grace → still shown.
			var after = svc.ApplyFetch(Empty, authoritative: -1, T0.AddMinutes(11));

			Assert.Equal(1, after);
		}
	}
}
