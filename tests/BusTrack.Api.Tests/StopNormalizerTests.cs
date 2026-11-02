using BusTrack.Api.Data;
using BusTrack.Api.Services;
using Xunit;

namespace BusTrack.Api.Tests;

public class StopNormalizerTests
{
    [Theory]
    [InlineData("Hisar Bus Stand", "hisar")]
    [InlineData("Fatehabad New Bus Stand", "fatehabad")]
    [InlineData("Tohana Bypass", "tohana")]
    [InlineData("Sirsa Chowk", "sirsa")]
    [InlineData("Barwala Old Bus Stand", "barwala")]
    [InlineData("Rohtak Terminal", "rohtak")]
    [InlineData("Delhi ISBT", "delhi")]
    public void NormalizeName_StripsTransitSuffixes(string input, string expected)
    {
        var normalized = StopNormalizer.NormalizeName(input);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("Hissar", "hisar")]       // 'ss' -> 's'
    [InlineData("Dhangar", "dhangar")]
    [InlineData("Dhanger", "dhangar")]     // 'er' -> 'ar'
    [InlineData("Bheema", "bhima")]        // 'ee' -> 'i'
    [InlineData("Poonia", "punia")]        // 'oo' -> 'u'
    public void NormalizeName_NormalizesIndianTransliterations(string input, string expected)
    {
        var normalized = StopNormalizer.NormalizeName(input);
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void AreSameStop_IdentifiesEquivalentStopsWithSlightVariation()
    {
        // Hisar Bus Stand vs Hisar
        var match = StopNormalizer.AreSameStop(
            "Hisar Bus Stand", "Hisar",
            cityA: "Hisar", cityB: "Hisar",
            latA: 29.1492, lonA: 75.7217,
            latB: 29.1500, lonB: 75.7220);

        Assert.True(match);
    }

    [Fact]
    public void AreSameStop_DistinguishesDifferentStops()
    {
        // Hisar vs Sirsa
        var match = StopNormalizer.AreSameStop(
            "Hisar Bus Stand", "Sirsa Bus Stand",
            cityA: "Hisar", cityB: "Sirsa",
            latA: 29.1492, lonA: 75.7217,
            latB: 29.5349, lonB: 75.0298);

        Assert.False(match);
    }

    [Fact]
    public void DeduplicateStopsList_CollapsesDuplicatesRetainingFirst()
    {
        var stops = new List<Stop>
        {
            new() { Id = 1, Name = "Hisar", Code = "HSR1", Latitude = 29.1492m, Longitude = 75.7217m, IsActive = true },
            new() { Id = 2, Name = "Hisar Bus Stand", Code = "HSR2", Latitude = 29.1495m, Longitude = 75.7220m, IsActive = true },
            new() { Id = 3, Name = "Barwala", Code = "BRW1", Latitude = 29.3804m, Longitude = 75.9125m, IsActive = true }
        };

        var deduped = StopNormalizer.DeduplicateStopsList(stops);

        Assert.Equal(2, deduped.Count);
        Assert.Contains(deduped, s => s.Id == 1);
        Assert.Contains(deduped, s => s.Id == 3);
        Assert.DoesNotContain(deduped, s => s.Id == 2);
    }
}
