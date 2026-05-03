using System;
using System.Collections.Generic;

namespace ATKTip.Data;

public static class DotDatabase
{
    public sealed class DotRule
    {
        public required double DurationSec { get; init; }
        public double RefreshBufferSec { get; init; } = 6.0;
        public double RefreshReadySec => Math.Max(0.0, DurationSec - RefreshBufferSec);
    }

    private static readonly Dictionary<string, DotRule> ByAbilityName =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Chaotic Spring"] = new() { DurationSec = 24.0 },
            ["Caustic Bite"] = new() { DurationSec = 45.0 },
            ["Stormbite"] = new() { DurationSec = 45.0 },
            ["Higanbana"] = new() { DurationSec = 60.0 },
            ["Dia"] = new() { DurationSec = 30.0 },
            ["Biolysis"] = new() { DurationSec = 30.0 },
            ["Combust III"] = new() { DurationSec = 30.0 },
            ["Eukrasian Dosis III"] = new() { DurationSec = 30.0 },
            ["High Thunder"] = new() { DurationSec = 30.0 },
            ["High Thunder II"] = new() { DurationSec = 24.0 },
        };

    public static IReadOnlyCollection<string> AbilityNames
        => ByAbilityName.Keys;

    public static DotRule? Lookup(string abilityName)
        => ByAbilityName.GetValueOrDefault(abilityName);
}
