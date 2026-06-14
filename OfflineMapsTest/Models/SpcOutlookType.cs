namespace OfflineMapsTest.Models
{
	/// <summary>
	/// The kind of SPC outlook product. Determines which days a product is valid for
	/// and (later) how it is styled on the map.
	/// </summary>
	public enum SpcOutlookType
	{
		Categorical,
		Tornado,
		Wind,
		Hail,
		ProbabilisticCombined,   // Day 3 combined probabilistic severe
		ExtendedProbabilistic,   // Days 4-8 probabilistic severe
		FireWeather,             // Day 1-2 fire weather
		ExtendedFireWeather      // Days 3-8 fire weather
	}
}
