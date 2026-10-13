using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Employee360.Integration.Processing;

/// <summary>Bound from configuration section "Locations" so aliases are data, not code.</summary>
public sealed class LocationOptions
{
    public const string SectionName = "Locations";

    /// <summary>Alias -> canonical name, e.g. "CPT" -> "Cape Town".</summary>
    public Dictionary<string, string> Aliases { get; set; } = new();
}

/// <summary>
/// Maps the many ways a location can be written to one canonical value.
/// Unknown values are NOT passed through: an unrecognised location is quarantined
/// so a human can add the alias, instead of polluting the Employee 360 view.
/// </summary>
public sealed class LocationNormaliser
{
    private readonly Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);

    public LocationNormaliser(IEnumerable<KeyValuePair<string, string>> aliases)
    {
        foreach (var (alias, canonical) in aliases)
        {
            _map[Clean(alias)] = canonical.Trim();
            _map[Clean(canonical)] = canonical.Trim(); // canonical names map to themselves
        }
    }

    public bool TryNormalise(string? raw, [NotNullWhen(true)] out string? canonical)
    {
        canonical = null;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        return _map.TryGetValue(Clean(raw), out canonical);
    }

    // Trim and collapse internal whitespace so " cape   town " still matches.
    private static string Clean(string value) =>
        Regex.Replace(value.Trim(), @"\s+", " ");
}
