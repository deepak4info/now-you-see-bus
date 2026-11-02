using System.Text.RegularExpressions;
using BusTrack.Api.Data;

namespace BusTrack.Api.Services;

public static class StopNormalizer
{
    private static readonly string[] SuffixesToRemove = new[]
    {
        " new bus stand", " old bus stand", " general bus stand",
        " bus stand", " bus stop", " bus depot", " isbt", " depot",
        " terminal", " bypass", " by pass", " chowk", " railway station", " rly station"
    };

    /// <summary>
    /// Standardizes stop names to catch transliteration and suffix variations
    /// (e.g. 'Dhanger' vs 'Dhangar', 'Hisar Bus Stand' vs 'Hisar', 'Hissar' vs 'Hisar').
    /// </summary>
    public static string NormalizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";

        var clean = name.Trim().ToLowerInvariant();

        // Strip common transit stop suffixes
        foreach (var suffix in SuffixesToRemove)
        {
            if (clean.EndsWith(suffix))
            {
                var stripped = clean[..^suffix.Length].Trim();
                if (stripped.Length >= 3)
                {
                    clean = stripped;
                    break;
                }
            }
        }

        // Collapse extra spaces and punctuation
        clean = Regex.Replace(clean, @"[\.\,\-\'\(\)]+", " ");
        clean = Regex.Replace(clean, @"\s+", " ").Trim();

        // Common Indian Romanization phonetic variations:
        // 'dhanger' -> 'dhangar', 'sunder' -> 'sundar'
        if (clean.EndsWith("er") && clean.Length > 4)
            clean = clean[..^2] + "ar";

        // 'hissar' -> 'hisar'
        clean = clean.Replace("ss", "s");
        clean = clean.Replace("ee", "i");
        clean = clean.Replace("oo", "u");
        clean = clean.Replace("ph", "f");

        return clean;
    }

    /// <summary>
    /// Checks whether two stop records refer to the same physical stop.
    /// </summary>
    public static bool AreSameStop(
        string nameA,
        string nameB,
        string? cityA = null,
        string? cityB = null,
        double? latA = null,
        double? lonA = null,
        double? latB = null,
        double? lonB = null)
    {
        if (string.IsNullOrWhiteSpace(nameA) || string.IsNullOrWhiteSpace(nameB))
            return false;

        var trimA = nameA.Trim();
        var trimB = nameB.Trim();

        // Exact case-insensitive match
        if (string.Equals(trimA, trimB, StringComparison.OrdinalIgnoreCase))
            return true;

        var normA = NormalizeName(trimA);
        var normB = NormalizeName(trimB);

        if (normA == normB)
            return true;

        // Prefix match e.g. "Fatehabad" and "Fatehabad Bypass"
        if (normA.Length >= 4 && normB.Length >= 4 && (normA.StartsWith(normB) || normB.StartsWith(normA)))
        {
            // If cities match or both coordinates are within 5 km
            if (SameCity(cityA, cityB) || AreCloseCoordinates(latA, lonA, latB, lonB, maxKm: 5.0))
                return true;
        }

        // Levenshtein distance <= 1 for names of length >= 5
        if (normA.Length >= 5 && normB.Length >= 5)
        {
            if (LevenshteinDistance(normA, normB) <= 1)
            {
                if (SameCity(cityA, cityB) || AreCloseCoordinates(latA, lonA, latB, lonB, maxKm: 8.0))
                    return true;
            }
        }

        return false;
    }

    private static bool SameCity(string? cityA, string? cityB)
    {
        if (string.IsNullOrWhiteSpace(cityA) || string.IsNullOrWhiteSpace(cityB))
            return true; // Unspecified city doesn't disqualify
        return string.Equals(cityA.Trim(), cityB.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool AreCloseCoordinates(double? latA, double? lonA, double? latB, double? lonB, double maxKm)
    {
        if (!latA.HasValue || !lonA.HasValue || !latB.HasValue || !lonB.HasValue)
            return true; // Missing coords don't disqualify if name is matched

        // Default or zero coords
        if (Math.Abs(latA.Value) < 0.1 || Math.Abs(latB.Value) < 0.1)
            return true;

        var dLat = (latB.Value - latA.Value) * Math.PI / 180.0;
        var dLon = (lonB.Value - lonA.Value) * Math.PI / 180.0;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(latA.Value * Math.PI / 180.0) * Math.Cos(latB.Value * Math.PI / 180.0) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        var distKm = 6371.0 * c;

        return distKm <= maxKm;
    }

    public static bool IsDemoOrAutoCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return true;
        var upper = code.Trim().ToUpperInvariant();
        return upper.StartsWith("DEMO-") || upper.StartsWith("TEST-") || upper.StartsWith("S-") || upper.Length > 12;
    }

    /// <summary>
    /// Deduplicates a list of stops in-memory so every returned record is unique.
    /// Prioritizes official codes, active status, and canonical names.
    /// </summary>
    public static List<Stop> DeduplicateStopsList(IEnumerable<Stop> stops)
    {
        var result = new List<Stop>();
        var seenNormalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Sort to evaluate official user-defined stops first before demo/auto ones
        var sorted = stops
            .OrderBy(s => IsDemoOrAutoCode(s.Code) ? 1 : 0)
            .ThenByDescending(s => s.Name.EndsWith("ar", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(s => s.Id)
            .ToList();

        foreach (var stop in sorted)
        {
            var code = stop.Code.Trim().ToUpperInvariant();
            var norm = NormalizeName(stop.Name);

            if (seenCodes.Contains(code))
                continue;

            // Check if another stop in result matches
            var alreadyExists = result.Any(existing =>
                AreSameStop(
                    existing.Name, stop.Name,
                    existing.City, stop.City,
                    (double)existing.Latitude, (double)existing.Longitude,
                    (double)stop.Latitude, (double)stop.Longitude));

            if (alreadyExists)
                continue;

            seenCodes.Add(code);
            seenNormalized.Add(norm);
            result.Add(stop);
        }

        return result.OrderBy(s => s.Name).ToList();
    }

    public static int LevenshteinDistance(string s, string t)
    {
        if (string.IsNullOrEmpty(s)) return string.IsNullOrEmpty(t) ? 0 : t.Length;
        if (string.IsNullOrEmpty(t)) return s.Length;

        var d = new int[s.Length + 1, t.Length + 1];
        for (var i = 0; i <= s.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= t.Length; j++) d[0, j] = j;

        for (var j = 1; j <= t.Length; j++)
        {
            for (var i = 1; i <= s.Length; i++)
            {
                var cost = s[i - 1] == t[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }
        return d[s.Length, t.Length];
    }
}
