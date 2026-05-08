using System;
using System.Collections.Generic;
using System.Linq;

namespace ATKTip.Data;

/// <summary>
/// Per-job gauge resource rules for validating that gauge-spending abilities
/// are only placed at sequence positions where sufficient gauge is achievable
/// based on preceding actions.
/// </summary>
public static class GaugeSimulator
{
    private static readonly HashSet<string> TrueGaugeResourceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Aetherflow",
        "Addersting",
        "Addersgall",
        "AstralSoul",
        "Battery",
        "Beast",
        "BeastChakra",
        "BlackPaint",
        "BlackMana",
        "Blood",
        "BloodLily",
        "Cartridge",
        "Chakra",
        "Coda",
        "Esprit",
        "FaeAether",
        "FirstBroodsGaze",
        "FirstmindsFocus",
        "FourfoldFeather",
        "Getsu",
        "Heat",
        "Ka",
        "Kazematoi",
        "Kenki",
        "LemureShroud",
        "Lily",
        "LunarNadi",
        "MP",
        "ManaStack",
        "Meditation",
        "Ninki",
        "Oath",
        "Palette",
        "Polyglot",
        "RattlingCoil",
        "SerpentOfferings",
        "Setsu",
        "Shroud",
        "SolarNadi",
        "Soul",
        "SoulVoice",
        "VoidShroud",
        "WhiteMana",
        "WhitePaint",
    };

    // ── Data types ─────────────────────────────────────────────────────────

    public sealed class GaugeResource
    {
        public string Name         { get; init; } = string.Empty;
        public int    MaxValue     { get; init; }
        public int    InitialValue { get; init; }
        public bool   AvoidOvercap { get; init; }
        /// <summary>When &gt; 0, this resource passively gains +1 every this many seconds during combat.</summary>
        public double PassiveGenerationIntervalSec { get; init; }
    }

    public sealed class GaugeEffect
    {
        public string GaugeName  { get; init; } = string.Empty;
        /// <summary>Minimum gauge value required to use this ability (0 = no check).</summary>
        public int    MinRequired { get; init; }
        /// <summary>Maximum gauge value allowed before use (int.MaxValue = no upper-bound check).</summary>
        public int    MaxAllowedBeforeUse { get; init; } = int.MaxValue;
        /// <summary>When set, the resource is refreshed directly to this value before applying Delta.</summary>
        public int?   SetValue { get; init; }
        /// <summary>Amount added to gauge after use; negative = consume.</summary>
        public int    Delta       { get; init; }
    }

    /// <summary>
    /// AST-specific card draw state rules.  Tracks which set of cards is
    /// currently available based on the last draw ability used.
    /// </summary>
    public sealed class CardDrawRules
    {
        public string          AstralDrawName { get; init; } = "Astral Draw";
        public string          UmbralDrawName { get; init; } = "Umbral Draw";
        /// <summary>Cards available only after Astral Draw.</summary>
        public HashSet<string> AstralCards    { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Cards available only after Umbral Draw.</summary>
        public HashSet<string> UmbralCards    { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class InstantCastRule
    {
        public string          ResourceName { get; init; } = string.Empty;
        public bool            AppliesToAnyCastTimeSpell { get; init; }
        public HashSet<string> AbilityNames { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public int             MinRequired { get; init; } = 1;
        public int             Consume { get; init; } = 1;
    }

    public sealed class HardcastGrantRule
    {
        public string          ResourceName { get; init; } = string.Empty;
        public bool            AppliesToAnyCastTimeSpell { get; init; }
        public HashSet<string> AbilityNames { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public int             Delta { get; init; } = 1;
    }

    public sealed class RepeatableGrantedActionRule
    {
        public string TriggerName { get; init; } = string.Empty;
        public string ResourceName { get; init; } = string.Empty;
        public int GrantCount { get; init; } = 1;
        public int ConsumeCount { get; init; } = 1;
        public bool TriggerConsumesWhenResourcePresent { get; init; }
        public bool SkipCooldownWhenConsuming { get; init; }
        public HashSet<string> ConsumerNames { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class JobGaugeRules
    {
        public IReadOnlyList<GaugeResource>                    Resources    { get; init; } = [];
        /// <summary>Key: ability name (case-insensitive) → one or more gauge effects.</summary>
        public Dictionary<string, IReadOnlyList<GaugeEffect>>  EffectByName { get; init; }
            = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Non-null only for Astrologian.</summary>
        public CardDrawRules? CardDraw { get; init; }
        public IReadOnlyList<InstantCastRule> InstantCastRules { get; init; } = [];
        public IReadOnlyList<HardcastGrantRule> HardcastGrantRules { get; init; } = [];
        public IReadOnlyList<RepeatableGrantedActionRule> RepeatableGrantedActionRules { get; init; } = [];
    }

    // ── Registry ──────────────────────────────────────────────────────────

    private static readonly Dictionary<string, JobGaugeRules> RulesBySpec =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Paladin"]     = BuildPaladinRules(),
            ["Reaper"]      = BuildReaperRules(),
            ["Warrior"]     = BuildWarriorRules(),
            ["Dark Knight"] = BuildDarkKnightRules(),
            ["Gunbreaker"]  = BuildGunbreakerRules(),
            ["Monk"]        = BuildMonkRules(),
            ["Ninja"]       = BuildNinjaRules(),
            ["Dragoon"]     = BuildDragoonRules(),
            ["Samurai"]     = BuildSamuraiRules(),
            ["Bard"]        = BuildBardRules(),
            ["Machinist"]   = BuildMachinistRules(),
            ["Dancer"]      = BuildDancerRules(),
            ["Pictomancer"] = BuildPictomancerRules(),
            ["Viper"]       = BuildViperRules(),
            ["Red Mage"]    = BuildRedMageRules(),
            ["Summoner"]    = BuildSummonerRules(),
            ["Scholar"]     = BuildScholarRules(),
            ["White Mage"]  = BuildWhiteMageRules(),
            ["Sage"]        = BuildSageRules(),
            ["Black Mage"]  = BuildBlackMageRules(),
            ["Astrologian"] = BuildAstrologianRules(),
        };

    internal static bool IsTrueGaugeResource(string resourceName)
        => TrueGaugeResourceNames.Contains(resourceName);

    internal static JobGaugeRules? GetMixedRules(string specName)
        => RulesBySpec.GetValueOrDefault(specName);

    /// <summary>Returns only real numeric gauge rules for <paramref name="specName"/>.</summary>
    public static JobGaugeRules? GetRules(string specName)
    {
        var mixed = GetMixedRules(specName);
        if (mixed == null)
            return null;

        var resources = mixed.Resources
            .Where(resource => IsTrueGaugeResource(resource.Name))
            .ToList();
        var allowed = resources
            .Select(resource => resource.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var effects = new Dictionary<string, IReadOnlyList<GaugeEffect>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (abilityName, abilityEffects) in mixed.EffectByName)
        {
            var filtered = abilityEffects
                .Where(effect => allowed.Contains(effect.GaugeName))
                .ToList();
            if (filtered.Count > 0)
                effects[abilityName] = filtered;
        }

        return new JobGaugeRules
        {
            Resources = resources,
            EffectByName = effects,
        };
    }

    // ── Shared builder helpers ────────────────────────────────────────────

    /// <summary>Single-resource effect shorthand.</summary>
    private static IReadOnlyList<GaugeEffect> E(string gaugeName, int delta, int minRequired = 0)
        => [new GaugeEffect { GaugeName = gaugeName, Delta = delta, MinRequired = minRequired }];

    private static void EnsureResource(
        List<GaugeResource> resources,
        string resourceName,
        int maxValue,
        int initialValue = 0,
        bool avoidOvercap = false,
        double passiveGenerationIntervalSec = 0.0)
    {
        if (resources.Any(r => string.Equals(r.Name, resourceName, StringComparison.OrdinalIgnoreCase)))
            return;

        resources.Add(new GaugeResource
        {
            Name = resourceName,
            MaxValue = maxValue,
            InitialValue = initialValue,
            AvoidOvercap = avoidOvercap,
            PassiveGenerationIntervalSec = passiveGenerationIntervalSec,
        });
    }

    private static void AppendEffects(
        Dictionary<string, IReadOnlyList<GaugeEffect>> effects,
        string abilityName,
        params GaugeEffect[] appended)
    {
        if (appended.Length == 0)
            return;

        if (effects.TryGetValue(abilityName, out var existing))
            effects[abilityName] = [.. existing, .. appended];
        else
            effects[abilityName] = [.. appended];
    }

    private static void AddSwiftcast(
        List<GaugeResource> resources,
        Dictionary<string, IReadOnlyList<GaugeEffect>> effects,
        List<InstantCastRule> instantCastRules)
    {
        EnsureResource(resources, "Swiftcast", 1);
        AddResourceGrant(effects, "Swiftcast", "Swiftcast");
        instantCastRules.Add(new InstantCastRule
        {
            ResourceName = "Swiftcast",
            AppliesToAnyCastTimeSpell = true,
        });
    }

    private static void AddGrantedAction(
        List<GaugeResource> resources,
        Dictionary<string, IReadOnlyList<GaugeEffect>> effects,
        string grantorName,
        string grantedName,
        int count = 1)
    {
        var resourceName = $"{grantedName} Ready";
        EnsureResource(resources, resourceName, count);
        AppendEffects(effects, grantorName, new GaugeEffect { GaugeName = resourceName, Delta = +count });
        AppendEffects(effects, grantedName, new GaugeEffect { GaugeName = resourceName, Delta = -1, MinRequired = 1 });
    }

    private static void AddGroupedGrantedActions(
        List<GaugeResource> resources,
        Dictionary<string, IReadOnlyList<GaugeEffect>> effects,
        string grantorName,
        IEnumerable<string> grantedNames,
        IEnumerable<string>? clearedNames = null)
    {
        foreach (var grantedName in grantedNames)
        {
            var resourceName = $"{grantedName} Ready";
            EnsureResource(resources, resourceName, 1);
            AppendEffects(effects, grantorName, new GaugeEffect { GaugeName = resourceName, Delta = +1 });
            AppendEffects(effects, grantedName, new GaugeEffect { GaugeName = resourceName, Delta = -1, MinRequired = 1 });
        }

        if (clearedNames == null)
            return;

        foreach (var clearedName in clearedNames)
        {
            var resourceName = $"{clearedName} Ready";
            EnsureResource(resources, resourceName, 1);
            AppendEffects(effects, grantorName, new GaugeEffect { GaugeName = resourceName, Delta = -1 });
        }
    }

    private static void AddResourceConsumer(
        Dictionary<string, IReadOnlyList<GaugeEffect>> effects,
        string consumerName,
        string resourceName,
        int minRequired = 1,
        int consume = 1)
        => AppendEffects(effects, consumerName,
            new GaugeEffect { GaugeName = resourceName, Delta = -consume, MinRequired = minRequired });

    private static void AddResourceGrant(
        Dictionary<string, IReadOnlyList<GaugeEffect>> effects,
        string grantorName,
        string resourceName,
        int delta = 1)
        => AppendEffects(effects, grantorName,
            new GaugeEffect { GaugeName = resourceName, Delta = delta });

    private static void AddRepeatableGrantedAction(
        List<GaugeResource> resources,
        List<RepeatableGrantedActionRule> rules,
        string triggerName,
        string resourceName,
        int maxValue,
        params string[] consumerNames)
    {
        EnsureResource(resources, resourceName, maxValue);
        rules.Add(new RepeatableGrantedActionRule
        {
            TriggerName = triggerName,
            ResourceName = resourceName,
            GrantCount = maxValue,
            TriggerConsumesWhenResourcePresent = true,
            SkipCooldownWhenConsuming = true,
            ConsumerNames = new HashSet<string>(consumerNames, StringComparer.OrdinalIgnoreCase),
        });
    }

    private static void AddInstantCastResource(
        List<GaugeResource> resources,
        Dictionary<string, IReadOnlyList<GaugeEffect>> effects,
        List<InstantCastRule> instantCastRules,
        string resourceName,
        string grantorName,
        int maxValue,
        int delta,
        params string[] instantAbilities)
    {
        EnsureResource(resources, resourceName, maxValue);
        AddResourceGrant(effects, grantorName, resourceName, delta);
        instantCastRules.Add(new InstantCastRule
        {
            ResourceName = resourceName,
            AbilityNames = new HashSet<string>(instantAbilities, StringComparer.OrdinalIgnoreCase),
        });
    }

    private static void AddHardcastGrantResource(
        List<GaugeResource> resources,
        List<HardcastGrantRule> hardcastGrantRules,
        string resourceName,
        int maxValue,
        string? abilityName = null,
        bool appliesToAnyCastTimeSpell = false,
        int delta = 1)
    {
        EnsureResource(resources, resourceName, maxValue);
        var rule = new HardcastGrantRule
        {
            ResourceName = resourceName,
            AppliesToAnyCastTimeSpell = appliesToAnyCastTimeSpell,
            Delta = delta,
        };
        if (!string.IsNullOrWhiteSpace(abilityName))
            rule.AbilityNames.Add(abilityName);
        hardcastGrantRules.Add(rule);
    }

    // —— Paladin (PLD) — MP-independent state + spell windows ——————————————————————————————
    private static JobGaugeRules BuildPaladinRules()
    {
        var effects = new Dictionary<string, IReadOnlyList<GaugeEffect>>(StringComparer.OrdinalIgnoreCase);
        var resources = new List<GaugeResource>();
        var instantCastRules = new List<InstantCastRule>();

        EnsureResource(resources, "Oath", 100, avoidOvercap: true, passiveGenerationIntervalSec: 0.448);
        AddGrantedAction(resources, effects, "Fight or Flight", "Goring Blade");

        EnsureResource(resources, "Atonement Ready", 1);
        EnsureResource(resources, "Supplication Ready", 1);
        EnsureResource(resources, "Sepulchre Ready", 1);
        EnsureResource(resources, "Divine Might", 1);
        EnsureResource(resources, "Requiescat", 5, initialValue: 5);
        EnsureResource(resources, "Blade of Faith Ready", 1);
        EnsureResource(resources, "Blade of Truth Ready", 1);
        EnsureResource(resources, "Blade of Valor Ready", 1);
        EnsureResource(resources, "Blade of Honor Ready", 1);

        AppendEffects(effects, "Prominence",
            new GaugeEffect { GaugeName = "Divine Might", Delta = +1 });
        AppendEffects(effects, "Royal Authority",
            new GaugeEffect { GaugeName = "Atonement Ready", Delta = +1 },
            new GaugeEffect { GaugeName = "Divine Might", Delta = +1 });
        AppendEffects(effects, "Atonement",
            new GaugeEffect { GaugeName = "Atonement Ready", Delta = -1, MinRequired = 1 },
            new GaugeEffect { GaugeName = "Supplication Ready", Delta = +1 });
        AppendEffects(effects, "Supplication",
            new GaugeEffect { GaugeName = "Supplication Ready", Delta = -1, MinRequired = 1 },
            new GaugeEffect { GaugeName = "Sepulchre Ready", Delta = +1 });
        AddResourceConsumer(effects, "Sepulchre", "Sepulchre Ready");

        AppendEffects(effects, "Requiescat",
            new GaugeEffect { GaugeName = "Requiescat", Delta = +5 });
        AppendEffects(effects, "Imperator",
            new GaugeEffect { GaugeName = "Requiescat", Delta = +5 });
        AppendEffects(effects, "Confiteor",
            new GaugeEffect { GaugeName = "Requiescat", Delta = -1, MinRequired = 1 },
            new GaugeEffect { GaugeName = "Blade of Faith Ready", Delta = +1 });
        AppendEffects(effects, "Blade of Faith",
            new GaugeEffect { GaugeName = "Blade of Faith Ready", Delta = -1, MinRequired = 1 },
            new GaugeEffect { GaugeName = "Blade of Truth Ready", Delta = +1 });
        AppendEffects(effects, "Blade of Truth",
            new GaugeEffect { GaugeName = "Blade of Truth Ready", Delta = -1, MinRequired = 1 },
            new GaugeEffect { GaugeName = "Blade of Valor Ready", Delta = +1 });
        AppendEffects(effects, "Blade of Valor",
            new GaugeEffect { GaugeName = "Blade of Valor Ready", Delta = -1, MinRequired = 1 },
            new GaugeEffect { GaugeName = "Blade of Honor Ready", Delta = +1 });
        AddResourceConsumer(effects, "Blade of Honor", "Blade of Honor Ready");
        effects["Sheltron"] = E("Oath", -50, minRequired: 50);
        effects["Holy Sheltron"] = E("Oath", -50, minRequired: 50);
        effects["Intervention"] = E("Oath", -50, minRequired: 50);
        effects["Cover"] = E("Oath", -50, minRequired: 50);

        instantCastRules.Add(new InstantCastRule
        {
            ResourceName = "Divine Might",
            AbilityNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Holy Spirit",
                "Holy Circle",
            },
        });
        instantCastRules.Add(new InstantCastRule
        {
            ResourceName = "Requiescat",
            AbilityNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Holy Spirit",
                "Holy Circle",
                "Confiteor",
                "Blade of Faith",
                "Blade of Truth",
                "Blade of Valor",
                "Blade of Honor",
            },
        });

        return new JobGaugeRules
        {
            Resources = resources,
            EffectByName = effects,
            InstantCastRules = instantCastRules,
        };
    }

    // —— Monk (MNK) — minimal Chakra legality ——————————————————————————————————————————————
    private static JobGaugeRules BuildMonkRules()
    {
        var effects = new Dictionary<string, IReadOnlyList<GaugeEffect>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Meditation"] = E("Chakra", +5),
            ["Steel Peak"] = E("Chakra", -5, minRequired: 5),
            ["Howling Fist"] = E("Chakra", -5, minRequired: 5),
            ["The Forbidden Chakra"] = E("Chakra", -5, minRequired: 5),
            ["Enlightenment"] = E("Chakra", -5, minRequired: 5),
            ["Riddle of Earth"] = E("Earth's Reply Ready", +1),
            ["Earth's Reply"] = E("Earth's Reply Ready", -1, minRequired: 1),
        };

        return new JobGaugeRules
        {
            Resources =
            [
                new GaugeResource { Name = "Chakra", MaxValue = 5, InitialValue = 0, AvoidOvercap = true },
                new GaugeResource { Name = "BeastChakra", MaxValue = 3, InitialValue = 0, AvoidOvercap = true },
                new GaugeResource { Name = "LunarNadi", MaxValue = 1, InitialValue = 0, AvoidOvercap = true },
                new GaugeResource { Name = "SolarNadi", MaxValue = 1, InitialValue = 0, AvoidOvercap = true },
                new GaugeResource { Name = "Earth's Reply Ready", MaxValue = 1, InitialValue = 0 },
            ],
            EffectByName = effects,
        };
    }

    // —— Reaper (RPR) — Soul Gauge ————————————————————————————————————————————————————————————

    // ── Reaper (RPR) — Soul Gauge ──────────────────────────────────────────
    //
    // Soul Gauge caps at 100 and starts at 0 each pull.
    //   Generators: combo hits (+10), Soul Slice / Soul Scythe (+50)
    //   Spenders:   Gibbet / Gallows / Guillotine (require ≥50, cost 50)
    //
    // Shroud Gauge excluded — too state-heavy to simulate reliably.
    private static JobGaugeRules BuildReaperRules()
    {
        var effects = new Dictionary<string, IReadOnlyList<GaugeEffect>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Slice"]            = E("Soul", +10),
            ["Waxing Slice"]     = E("Soul", +10),
            ["Infernal Slice"]   = E("Soul", +10),
            ["Spinning Scythe"]  = E("Soul", +10),
            ["Nightmare Scythe"] = E("Soul", +10),
            ["Soul Slice"]       = E("Soul", +50),
            ["Soul Scythe"]      = E("Soul", +50),
            ["Blood Stalk"]      =
            [
                new GaugeEffect { GaugeName = "Soul", Delta = -50, MinRequired = 50 },
                new GaugeEffect { GaugeName = "Soul Reaver", Delta = +1 },
            ],
            ["Gibbet"]           =
            [
                new GaugeEffect { GaugeName = "Soul", Delta = -50, MinRequired = 50 },
                new GaugeEffect { GaugeName = "Shroud", Delta = +10 },
                new GaugeEffect { GaugeName = "Soul Reaver", Delta = -1, MinRequired = 1 },
                new GaugeEffect { GaugeName = "Enhanced Gallows", Delta = +1 },
            ],
            ["Gallows"]          =
            [
                new GaugeEffect { GaugeName = "Soul", Delta = -50, MinRequired = 50 },
                new GaugeEffect { GaugeName = "Shroud", Delta = +10 },
                new GaugeEffect { GaugeName = "Soul Reaver", Delta = -1, MinRequired = 1 },
                new GaugeEffect { GaugeName = "Enhanced Gibbet", Delta = +1 },
            ],
            ["Guillotine"]       =
            [
                new GaugeEffect { GaugeName = "Soul", Delta = -50, MinRequired = 50 },
                new GaugeEffect { GaugeName = "Shroud", Delta = +10 },
            ],
            ["Soulsow"]          = E("Harvest Moon Ready", +1),
            ["Harvest Moon"]     = E("Harvest Moon Ready", -1, minRequired: 1),
            ["Perfectio"]        = E("Perfectio Parata", -1, minRequired: 1),
            ["Hell's Ingress"]   = E("Enhanced Harpe", +1),
            ["Hell's Egress"]    = E("Enhanced Harpe", +1),
            ["Plentiful Harvest"] = E("Shroud", +50),
            ["Enshroud"] =
            [
                new GaugeEffect { GaugeName = "Shroud", Delta = -50, MinRequired = 50 },
                new GaugeEffect { GaugeName = "Enshrouded", Delta = +1 },
                new GaugeEffect { GaugeName = "LemureShroud", Delta = +5 },
                new GaugeEffect { GaugeName = "Sacrificium Ready", Delta = +1 },
                new GaugeEffect { GaugeName = "Oblatio", Delta = +1 },
            ],
            ["Void Reaping"] =
            [
                new GaugeEffect { GaugeName = "Enshrouded", Delta = 0, MinRequired = 1 },
                new GaugeEffect { GaugeName = "LemureShroud", Delta = -1, MinRequired = 1 },
                new GaugeEffect { GaugeName = "VoidShroud", Delta = +1 },
            ],
            ["Cross Reaping"] =
            [
                new GaugeEffect { GaugeName = "Enshrouded", Delta = 0, MinRequired = 1 },
                new GaugeEffect { GaugeName = "LemureShroud", Delta = -1, MinRequired = 1 },
                new GaugeEffect { GaugeName = "VoidShroud", Delta = +1 },
            ],
            ["Lemure's Slice"] =
            [
                new GaugeEffect { GaugeName = "VoidShroud", Delta = -2, MinRequired = 2 },
            ],
            ["Communio"] =
            [
                new GaugeEffect { GaugeName = "Enshrouded", Delta = -1, MinRequired = 1 },
                new GaugeEffect { GaugeName = "LemureShroud", Delta = -5, MinRequired = 1 },
                new GaugeEffect { GaugeName = "Perfectio Parata", Delta = +1 },
            ],
            ["Gluttony"] =
            [
                new GaugeEffect { GaugeName = "Soul", Delta = -50, MinRequired = 50 },
                new GaugeEffect { GaugeName = "Executioner", Delta = +2 },
            ],
            ["Unveiled Gibbet"] =
            [
                new GaugeEffect { GaugeName = "Soul", Delta = -50, MinRequired = 50 },
                new GaugeEffect { GaugeName = "Enhanced Gibbet", Delta = -1, MinRequired = 1 },
                new GaugeEffect { GaugeName = "Soul Reaver", Delta = +1 },
            ],
            ["Unveiled Gallows"] =
            [
                new GaugeEffect { GaugeName = "Soul", Delta = -50, MinRequired = 50 },
                new GaugeEffect { GaugeName = "Enhanced Gallows", Delta = -1, MinRequired = 1 },
                new GaugeEffect { GaugeName = "Soul Reaver", Delta = +1 },
            ],
            ["Executioner's Gibbet"] =
            [
                new GaugeEffect { GaugeName = "Executioner", Delta = -1, MinRequired = 1 },
                new GaugeEffect { GaugeName = "Shroud", Delta = +10 },
                new GaugeEffect { GaugeName = "Enhanced Gallows", Delta = +1 },
            ],
            ["Executioner's Gallows"] =
            [
                new GaugeEffect { GaugeName = "Executioner", Delta = -1, MinRequired = 1 },
                new GaugeEffect { GaugeName = "Shroud", Delta = +10 },
                new GaugeEffect { GaugeName = "Enhanced Gibbet", Delta = +1 },
            ],
            ["Sacrificium"] =
            [
                new GaugeEffect { GaugeName = "Sacrificium Ready", Delta = -1, MinRequired = 1 },
                new GaugeEffect { GaugeName = "Oblatio", Delta = -1, MinRequired = 1 },
                new GaugeEffect { GaugeName = "Enshrouded", Delta = 0, MinRequired = 1 },
            ],
        };

        var resources = new List<GaugeResource>
        {
            new() { Name = "Soul", MaxValue = 100, InitialValue = 0, AvoidOvercap = true },
            new() { Name = "Shroud", MaxValue = 100, InitialValue = 0, AvoidOvercap = true },
            new() { Name = "LemureShroud", MaxValue = 5, InitialValue = 0, AvoidOvercap = true },
            new() { Name = "VoidShroud", MaxValue = 5, InitialValue = 0, AvoidOvercap = true },
            new() { Name = "Soul Reaver", MaxValue = 2, InitialValue = 0 },
            new() { Name = "Harvest Moon Ready", MaxValue = 1, InitialValue = 1 },
            new() { Name = "Sacrificium Ready", MaxValue = 1, InitialValue = 0 },
            new() { Name = "Enhanced Harpe", MaxValue = 1, InitialValue = 0 },
            new() { Name = "Enshrouded", MaxValue = 1, InitialValue = 0 },
            new() { Name = "Executioner", MaxValue = 2, InitialValue = 0 },
            new() { Name = "Enhanced Gibbet", MaxValue = 1, InitialValue = 0 },
            new() { Name = "Enhanced Gallows", MaxValue = 1, InitialValue = 0 },
            new() { Name = "Oblatio", MaxValue = 1, InitialValue = 0 },
            new() { Name = "Perfectio Parata", MaxValue = 1, InitialValue = 0 },
        };

        var instantCastRules = new List<InstantCastRule>
        {
            new()
            {
                ResourceName = "Enhanced Harpe",
                AbilityNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Harpe" },
            },
        };

        return new JobGaugeRules
        {
            Resources    = resources,
            EffectByName = effects,
            InstantCastRules = instantCastRules,
        };
    }

    // ── Warrior (WAR) — Beast Gauge ────────────────────────────────────────
    //
    // Beast Gauge: 0–100, starts at 0.
    //   Generators: combo hits (+10/+20)
    //   Spenders:   Fell Cleave / Decimate (≥50, cost 50)
    //   Inner Release: Fell Cleave → Inner Chaos (free), Decimate → Chaotic Cyclone (free)
    //   ► False positives: the 3 free Fell Cleaves during Inner Release may trigger gauge warnings.
    private static JobGaugeRules BuildWarriorRules()
    {
        var effects = new Dictionary<string, IReadOnlyList<GaugeEffect>>(StringComparer.OrdinalIgnoreCase)
        {
            // ST combo
            ["Heavy Swing"]     = E("Beast", 0),
            ["Maim"]            = E("Beast", +10),
            ["Storm's Path"]    = E("Beast", +20),
            ["Storm's Eye"]     = E("Beast", +10),
            // AoE combo
            ["Overpower"]       = E("Beast", 0),
            ["Mythril Tempest"] = E("Beast", +20),
            ["Infuriate"]       = E("Beast", +50),
            // Spenders (need ≥50)
            ["Fell Cleave"]     = E("Beast", -50, minRequired: 50),
            ["Decimate"]        = E("Beast", -50, minRequired: 50),
            // Inner Release replacements — free, no cost
            ["Inner Chaos"]     = E("Beast", 0),
            ["Chaotic Cyclone"] = E("Beast", 0),
            // Other
            ["Primal Rend"]     = E("Beast", 0),
            ["Primal Ruination"]= E("Beast", 0),
            ["Inner Release"]   = E("Primal Wrath Ready", +1),
            ["Primal Wrath"]    = E("Primal Wrath Ready", -1, minRequired: 1),
        };

        var resources = new List<GaugeResource>
        {
            new() { Name = "Beast", MaxValue = 100, InitialValue = 0, AvoidOvercap = true },
            new() { Name = "Primal Wrath Ready", MaxValue = 1, InitialValue = 0 },
        };
        var repeatableGrantedActionRules = new List<RepeatableGrantedActionRule>();

        AddRepeatableGrantedAction(
            resources,
            repeatableGrantedActionRules,
            "Inner Release",
            "Inner Release",
            3,
            "Fell Cleave",
            "Decimate");

        return new JobGaugeRules
        {
            Resources    = resources,
            EffectByName = effects,
            RepeatableGrantedActionRules = repeatableGrantedActionRules,
        };
    }

    // ── Dark Knight (DRK) — Blood Gauge + MP ───────────────────────────────
    //
    // Blood Gauge: 0–100, starts at 0.
    //   Generators: combo hits (+10/+20)
    //   Spenders:   Bloodspiller / Quietus (≥50, cost 50)
    // MP: 0–10000, starts full.
    //   Generators: Syphon Strike +600, Stalwart Soul +600, Comeuppance +200,
    //               Torcleaver +200, Impalement +500, Carve and Spit +600,
    //               Abyssal Drain +600
    //   Spenders:   Edge of Shadow / Flood of Shadow / The Blackest Night
    //               (≥3000, cost 3000)
    //   Delirium: at level 100 Delirium transforms Bloodspiller into a free 3-hit chain
    //   (Scarlet Delirium → Comeuppance → Torcleaver). These are flagged as 0-cost.
    //   ► False positives possible if Bloodspiller is listed without the Delirium replacements.
    private static JobGaugeRules BuildDarkKnightRules()
    {
        var effects = new Dictionary<string, IReadOnlyList<GaugeEffect>>(StringComparer.OrdinalIgnoreCase)
        {
            // ST combo
            ["Hard Slash"]       = E("Blood", 0),
            ["Syphon Strike"]    =
            [
                new GaugeEffect { GaugeName = "Blood", Delta = 0 },
                new GaugeEffect { GaugeName = "MP", Delta = +600 },
            ],
            ["Souleater"]        = E("Blood", +20),
            // AoE combo
            ["Unleash"]          = E("Blood", 0),
            ["Stalwart Soul"]    =
            [
                new GaugeEffect { GaugeName = "Blood", Delta = +20 },
                new GaugeEffect { GaugeName = "MP", Delta = +600 },
            ],
            // Spenders (need ≥50)
            ["Bloodspiller"]     = E("Blood", -50, minRequired: 50),
            ["Quietus"]          = E("Blood", -50, minRequired: 50),
            // Level-100 Delirium chain — free, no Blood cost
            ["Delirium"]         =
            [
                new GaugeEffect { GaugeName = "Delirium", Delta = +3 },
                new GaugeEffect { GaugeName = "Scarlet Delirium Ready", Delta = +1 },
            ],
            ["Scarlet Delirium"] =
            [
                new GaugeEffect { GaugeName = "Blood", Delta = 0 },
                new GaugeEffect { GaugeName = "Scarlet Delirium Ready", Delta = -1, MinRequired = 1 },
                new GaugeEffect { GaugeName = "Comeuppance Ready", Delta = +1 },
            ],
            ["Comeuppance"]      =
            [
                new GaugeEffect { GaugeName = "Blood", Delta = 0 },
                new GaugeEffect { GaugeName = "MP", Delta = +200 },
                new GaugeEffect { GaugeName = "Comeuppance Ready", Delta = -1, MinRequired = 1 },
                new GaugeEffect { GaugeName = "Torcleaver Ready", Delta = +1 },
            ],
            ["Torcleaver"]       =
            [
                new GaugeEffect { GaugeName = "Blood", Delta = 0 },
                new GaugeEffect { GaugeName = "MP", Delta = +200 },
                new GaugeEffect { GaugeName = "Torcleaver Ready", Delta = -1, MinRequired = 1 },
            ],
            ["Impalement"]       =
            [
                new GaugeEffect { GaugeName = "Blood", Delta = 0 },
                new GaugeEffect { GaugeName = "MP", Delta = +500 },
            ],   // AoE Delirium finisher
            ["Salted Earth"]     = E("Salt and Darkness Ready", +1),
            ["Salt and Darkness"]= E("Salt and Darkness Ready", -1, minRequired: 1),
            ["Edge of Shadow"]   = E("MP", -3000, minRequired: 3000),
            ["Flood of Shadow"]  = E("MP", -3000, minRequired: 3000),
            ["Carve and Spit"]   = E("MP", +600),
            ["Abyssal Drain"]    = E("MP", +600),
            ["The Blackest Night"] = E("MP", -3000, minRequired: 3000),
        };

        var resources = new List<GaugeResource>
        {
            new() { Name = "Blood", MaxValue = 100, InitialValue = 0, AvoidOvercap = true },
            new() { Name = "MP", MaxValue = 10000, InitialValue = 10000, AvoidOvercap = true },
            new() { Name = "Salt and Darkness Ready", MaxValue = 1, InitialValue = 0 },
            new() { Name = "Delirium", MaxValue = 3, InitialValue = 0 },
            new() { Name = "Scarlet Delirium Ready", MaxValue = 1, InitialValue = 0 },
            new() { Name = "Comeuppance Ready", MaxValue = 1, InitialValue = 0 },
            new() { Name = "Torcleaver Ready", MaxValue = 1, InitialValue = 0 },
        };
        var repeatableGrantedActionRules = new List<RepeatableGrantedActionRule>();
        AddRepeatableGrantedAction(resources, repeatableGrantedActionRules, "Delirium", "Delirium", 3, "Bloodspiller", "Quietus");

        return new JobGaugeRules
        {
            Resources = resources,
            EffectByName = effects,
            RepeatableGrantedActionRules = repeatableGrantedActionRules,
        };
    }

    // ── Gunbreaker (GNB) — Powder Gauge (cartridges) ───────────────────────
    //
    // Cartridges: 0–3, starts at 0.
    //   Generators: Solid Barrel +1, Demon Slaughter +1, Bloodfest +3
    //   Spenders:   Gnashing Fang / Burst Strike / Fated Circle (≥1, cost 1),
    //               Double Down (≥2, cost 2)
    private static JobGaugeRules BuildGunbreakerRules()
    {
        var effects = new Dictionary<string, IReadOnlyList<GaugeEffect>>(StringComparer.OrdinalIgnoreCase)
        {
            // Generators
            ["Solid Barrel"]    = E("Cartridge", +1),
            ["Demon Slaughter"] = E("Cartridge", +1),
            ["Bloodfest"]       = E("Cartridge", +3),  // clamped to max 3
            // Spenders
            ["Gnashing Fang"]   =
            [
                new GaugeEffect { GaugeName = "Cartridge", Delta = -1, MinRequired = 1 },
                new GaugeEffect { GaugeName = "Ready to Rip", Delta = +1 },
            ],
            ["Burst Strike"]    =
            [
                new GaugeEffect { GaugeName = "Cartridge", Delta = -1, MinRequired = 1 },
                new GaugeEffect { GaugeName = "Ready to Blast", Delta = +1 },
            ],
            ["Fated Circle"]    =
            [
                new GaugeEffect { GaugeName = "Cartridge", Delta = -1, MinRequired = 1 },
                new GaugeEffect { GaugeName = "Ready to Raze", Delta = +1 },
            ],
            ["Double Down"]     = E("Cartridge", -2, minRequired: 2),
            // Gnashing Fang combo follow-ups — no cartridge cost
            ["Savage Claw"]     =
            [
                new GaugeEffect { GaugeName = "Cartridge", Delta = 0 },
                new GaugeEffect { GaugeName = "Ready to Tear", Delta = +1 },
            ],
            ["Wicked Talon"]    =
            [
                new GaugeEffect { GaugeName = "Cartridge", Delta = 0 },
                new GaugeEffect { GaugeName = "Ready to Gouge", Delta = +1 },
            ],
            // Level-100 follow-ups — no cartridge cost
            ["Reign of Beasts"] =
            [
                new GaugeEffect { GaugeName = "Cartridge", Delta = 0 },
                new GaugeEffect { GaugeName = "Ready to Reign", Delta = -1, MinRequired = 1 },
            ],
            ["Noble Blood"]     = E("Cartridge", 0),
            ["Lion Heart"]      = E("Cartridge", 0),
            ["Jugular Rip"]     = E("Ready to Rip", -1, minRequired: 1),
            ["Abdomen Tear"]    = E("Ready to Tear", -1, minRequired: 1),
            ["Eye Gouge"]       = E("Ready to Gouge", -1, minRequired: 1),
            ["Hypervelocity"]   = E("Ready to Blast", -1, minRequired: 1),
            ["Fated Brand"]     = E("Ready to Raze", -1, minRequired: 1),
            ["Bloodfest"]       =
            [
                new GaugeEffect { GaugeName = "Cartridge", Delta = +3 },
                new GaugeEffect { GaugeName = "Ready to Reign", Delta = +1 },
            ],
        };

        return new JobGaugeRules
        {
            Resources =
            [
                new GaugeResource { Name = "Cartridge", MaxValue = 3, InitialValue = 0, AvoidOvercap = true },
                new GaugeResource { Name = "Ready to Rip", MaxValue = 1, InitialValue = 0 },
                new GaugeResource { Name = "Ready to Tear", MaxValue = 1, InitialValue = 0 },
                new GaugeResource { Name = "Ready to Gouge", MaxValue = 1, InitialValue = 0 },
                new GaugeResource { Name = "Ready to Blast", MaxValue = 1, InitialValue = 0 },
                new GaugeResource { Name = "Ready to Raze", MaxValue = 1, InitialValue = 0 },
                new GaugeResource { Name = "Ready to Reign", MaxValue = 1, InitialValue = 0 },
            ],
            EffectByName = effects,
        };
    }

    // ── Ninja (NIN) — Ninki ────────────────────────────────────────────────
    //
    // Ninki: 0–100, starts at 0.
    //   Generators: combo hits (+5/+15), select oGCDs (+10)
    //   Spenders:   Bhavacakra / Hellfrog / Zesho Meppo / Deathfrog (≥50, cost 50)
    private static JobGaugeRules BuildNinjaRules()
    {
        var effects = new Dictionary<string, IReadOnlyList<GaugeEffect>>(StringComparer.OrdinalIgnoreCase)
        {
            // ST combo
            ["Spinning Edge"]       = E("Ninki", +5),
            ["Gust Slash"]          = E("Ninki", +5),
            ["Aeolian Edge"]        =
            [
                new GaugeEffect { GaugeName = "Ninki", Delta = +15 },
                new GaugeEffect { GaugeName = "Kazematoi", Delta = -1 },
            ],
            ["Armor Crush"]         =
            [
                new GaugeEffect { GaugeName = "Ninki", Delta = +15 },
                new GaugeEffect { GaugeName = "Kazematoi", Delta = +2 },
            ],
            // AoE combo
            ["Death Blossom"]       = E("Ninki", +5),
            ["Hakke Mujinsatsu"]    = E("Ninki", +5),
            // Other GCDs
            ["Shadow Fang"]         = E("Ninki", 0),
            ["Phantom Kamaitachi"]  = E("Ninki", +10),
            ["Dokumori"]            = E("Ninki", +40),
            ["Meisui"]              = E("Ninki", +50),
            ["Bunshin"]             = E("Ninki", -50, minRequired: 50),
            // Spenders (need ≥50)
            ["Bhavacakra"]          = E("Ninki", -50, minRequired: 50),
            ["Hellfrog Medium"]     = E("Ninki", -50, minRequired: 50),
            ["Zesho Meppo"]         = E("Ninki", -50, minRequired: 50),
            ["Deathfrog Medium"]    = E("Ninki", -50, minRequired: 50),
        };
        var resources = new List<GaugeResource>
        {
            new() { Name = "Ninki", MaxValue = 100, InitialValue = 0, AvoidOvercap = true },
            new() { Name = "Kazematoi", MaxValue = 5, InitialValue = 0, AvoidOvercap = true },
        };
        AddGrantedAction(resources, effects, "Bunshin", "Phantom Kamaitachi");

        return new JobGaugeRules
        {
            Resources    = resources,
            EffectByName = effects,
        };
    }

    // ── Dragoon (DRG) — Firstminds' Focus ─────────────────────────────────
    //
    // Firstminds' Focus: 0–2, starts at 0.
    //   Generators: positional combo enders (Fang and Claw, Wheeling Thrust, Drakesbane) +1 each
    //   Spender:    Wyrmwind Thrust (≥2, cost 2)
    private static JobGaugeRules BuildDragoonRules()
    {
        var effects = new Dictionary<string, IReadOnlyList<GaugeEffect>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Fang and Claw"]  = E("FirstmindsFocus", 0),
            ["Wheeling Thrust"]= E("FirstmindsFocus", 0),
            ["Drakesbane"]     = E("FirstmindsFocus", 0),
            ["Raiden Thrust"]  = E("FirstmindsFocus", +1),
            ["Draconian Fury"] = E("FirstmindsFocus", +1),
            ["Wyrmwind Thrust"]= E("FirstmindsFocus", -2, minRequired: 2),
            ["Jump"]           = E("Mirage Dive Ready", +1),
            ["High Jump"]      = E("Mirage Dive Ready", +1),
            ["Mirage Dive"]    =
            [
                new GaugeEffect { GaugeName = "Mirage Dive Ready", Delta = -1, MinRequired = 1 },
                new GaugeEffect { GaugeName = "FirstBroodsGaze", Delta = +1 },
            ],
        };
        var resources = new List<GaugeResource>
        {
            new() { Name = "FirstBroodsGaze", MaxValue = 2, InitialValue = 0, AvoidOvercap = true },
            new() { Name = "FirstmindsFocus", MaxValue = 2, InitialValue = 0, AvoidOvercap = true },
            new() { Name = "Mirage Dive Ready", MaxValue = 1, InitialValue = 0 },
            new() { Name = "Draconian Fire", MaxValue = 1, InitialValue = 0 },
            new() { Name = "Life of the Dragon", MaxValue = 1, InitialValue = 0 },
            new() { Name = "Nastrond Ready", MaxValue = 3, InitialValue = 0 },
            new() { Name = "Stardiver Ready", MaxValue = 1, InitialValue = 0 },
            new() { Name = "Starcross Ready", MaxValue = 1, InitialValue = 0 },
            new() { Name = "Rise of the Dragon Ready", MaxValue = 1, InitialValue = 0 },
        };
        var repeatableGrantedActionRules = new List<RepeatableGrantedActionRule>();

        AppendEffects(effects, "Fang and Claw",
            new GaugeEffect { GaugeName = "Draconian Fire", Delta = +1 });
        AppendEffects(effects, "Wheeling Thrust",
            new GaugeEffect { GaugeName = "Draconian Fire", Delta = +1 });
        AppendEffects(effects, "Drakesbane",
            new GaugeEffect { GaugeName = "Draconian Fire", Delta = +1 });
        AppendEffects(effects, "Raiden Thrust",
            new GaugeEffect { GaugeName = "Draconian Fire", Delta = -1, MinRequired = 1 });
        AppendEffects(effects, "Draconian Fury",
            new GaugeEffect { GaugeName = "Draconian Fire", Delta = -1, MinRequired = 1 });
        AddRepeatableGrantedAction(
            resources,
            repeatableGrantedActionRules,
            "Geirskogul",
            "Nastrond Ready",
            3,
            "Nastrond");
        AppendEffects(effects, "Geirskogul",
            new GaugeEffect { GaugeName = "FirstBroodsGaze", Delta = -2 },
            new GaugeEffect { GaugeName = "Life of the Dragon", Delta = +1 },
            new GaugeEffect { GaugeName = "Stardiver Ready", Delta = +1 });
        AppendEffects(effects, "Stardiver",
            new GaugeEffect { GaugeName = "Stardiver Ready", Delta = -1, MinRequired = 1 },
            new GaugeEffect { GaugeName = "Starcross Ready", Delta = +1 });
        AppendEffects(effects, "Dragonfire Dive",
            new GaugeEffect { GaugeName = "Rise of the Dragon Ready", Delta = +1 });
        AppendEffects(effects, "Rise of the Dragon",
            new GaugeEffect { GaugeName = "Rise of the Dragon Ready", Delta = -1, MinRequired = 1 });
        AppendEffects(effects, "Starcross",
            new GaugeEffect { GaugeName = "Starcross Ready", Delta = -1, MinRequired = 1 });

        return new JobGaugeRules
        {
            Resources = resources,
            EffectByName = effects,
            RepeatableGrantedActionRules = repeatableGrantedActionRules,
        };
    }

    // ── Samurai (SAM) — Kenki ──────────────────────────────────────────────
    //
    // Kenki: 0–100, starts at 0.
    //   Generators: combo hits (+5/+15/+10)
    //   Spenders:   Hissatsu abilities (25 or 50 per use)
    private static JobGaugeRules BuildSamuraiRules()
    {
        var effects = new Dictionary<string, IReadOnlyList<GaugeEffect>>(StringComparer.OrdinalIgnoreCase)
        {
            // ST combo
            ["Hakaze"]              = E("Kenki", +5),
            ["Gyofu"]               = E("Kenki", +5),   // level 96 upgrade of Hakaze
            ["Jinpu"]               = E("Kenki", +5),
            ["Shifu"]               = E("Kenki", +5),
            ["Yukikaze"]            = E("Kenki", +15),
            ["Gekko"]               = E("Kenki", +10),
            ["Kasha"]               = E("Kenki", +10),
            // AoE combo
            ["Fuga"]                = E("Kenki", +5),
            ["Fuko"]                = E("Kenki", +5),
            ["Mangetsu"]            = E("Kenki", +10),
            ["Oka"]                 = E("Kenki", +10),
            // Special GCDs
            ["Ogi Namikiri"]        = E("Kenki", 0),
            // Kenki spenders
            ["Hissatsu: Shinten"]   = E("Kenki", -25, minRequired: 25),
            ["Hissatsu: Kyuten"]    = E("Kenki", -25, minRequired: 25),
            ["Hissatsu: Senei"]     = E("Kenki", -25, minRequired: 25),
            ["Hissatsu: Guren"]     = E("Kenki", -25, minRequired: 25),
            ["Hissatsu: Gyoten"]    = E("Kenki", -10, minRequired: 10),
            ["Hissatsu: Yaten"]     = E("Kenki", -10, minRequired: 10),
            ["Zanshin"]             = E("Kenki", -50, minRequired: 50),  // level 96
        };
        var resources = new List<GaugeResource>
        {
            new() { Name = "Kenki", MaxValue = 100, InitialValue = 0, AvoidOvercap = true },
            new() { Name = "Meditation", MaxValue = 3, InitialValue = 0, AvoidOvercap = true },
            new() { Name = "Setsu", MaxValue = 1, InitialValue = 0, AvoidOvercap = true },
            new() { Name = "Getsu", MaxValue = 1, InitialValue = 0, AvoidOvercap = true },
            new() { Name = "Ka", MaxValue = 1, InitialValue = 0, AvoidOvercap = true },
        };
        AppendEffects(effects, "Yukikaze", new GaugeEffect { GaugeName = "Setsu", Delta = +1 });
        AppendEffects(effects, "Gekko", new GaugeEffect { GaugeName = "Getsu", Delta = +1 });
        AppendEffects(effects, "Mangetsu", new GaugeEffect { GaugeName = "Getsu", Delta = +1 });
        AppendEffects(effects, "Kasha", new GaugeEffect { GaugeName = "Ka", Delta = +1 });
        AppendEffects(effects, "Oka", new GaugeEffect { GaugeName = "Ka", Delta = +1 });
        AddResourceGrant(effects, "Higanbana", "Meditation");
        AddResourceGrant(effects, "Tenka Goken", "Meditation");
        AddResourceGrant(effects, "Midare Setsugekka", "Meditation");
        AddResourceGrant(effects, "Ogi Namikiri", "Meditation");
        AppendEffects(effects, "Midare Setsugekka",
            new GaugeEffect { GaugeName = "Setsu", Delta = -1, MinRequired = 1 },
            new GaugeEffect { GaugeName = "Getsu", Delta = -1, MinRequired = 1 },
            new GaugeEffect { GaugeName = "Ka", Delta = -1, MinRequired = 1 });
        AddGrantedAction(resources, effects, "Ikishoten", "Ogi Namikiri");

        return new JobGaugeRules
        {
            Resources    = resources,
            EffectByName = effects,
        };
    }

    // ── Machinist (MCH) — Heat Gauge + Battery Gauge ───────────────────────
    //
    // Heat: 0–100 — Hypercharge costs 50; Heat Blast/Auto Crossbow don't cost Heat.
    // Battery: 0–100 — Automaton Queen summon costs 50 (consumes all battery ≥50).
    private static JobGaugeRules BuildBardRules()
    {
        var effects = new Dictionary<string, IReadOnlyList<GaugeEffect>>(StringComparer.OrdinalIgnoreCase);
        var resources = new List<GaugeResource>();

        EnsureResource(resources, "SoulVoice", 100, avoidOvercap: true);
        EnsureResource(resources, "Refulgent Arrow Ready", 1);
        EnsureResource(resources, "Shadowbite Ready", 1);
        EnsureResource(resources, "Blast Arrow Ready", 1);
        EnsureResource(resources, "Resonant Arrow Ready", 1);
        EnsureResource(resources, "Radiant Encore Ready", 1);
        EnsureResource(resources, "Coda", 3, initialValue: 0, avoidOvercap: true);

        AddResourceGrant(effects, "Burst Shot", "Refulgent Arrow Ready");
        AddResourceGrant(effects, "Barrage", "Refulgent Arrow Ready");
        AddResourceGrant(effects, "Ladonsbite", "Shadowbite Ready");
        AppendEffects(effects, "Apex Arrow",
            new GaugeEffect { GaugeName = "SoulVoice", Delta = -100, MinRequired = 20 },
            new GaugeEffect { GaugeName = "Blast Arrow Ready", Delta = +1 });
        AddResourceGrant(effects, "Barrage", "Resonant Arrow Ready");
        AddResourceGrant(effects, "Wanderer's Minuet", "Coda");
        AddResourceGrant(effects, "Mage's Ballad", "Coda");
        AddResourceGrant(effects, "Army's Paeon", "Coda");
        AppendEffects(effects, "Radiant Finale",
            new GaugeEffect { GaugeName = "Coda", Delta = -3, MinRequired = 1 },
            new GaugeEffect { GaugeName = "Radiant Encore Ready", Delta = +1 });
        AppendEffects(effects, "Blast Arrow",
            new GaugeEffect { GaugeName = "Blast Arrow Ready", Delta = -1, MinRequired = 1 },
            new GaugeEffect { GaugeName = "Resonant Arrow Ready", Delta = +1 });
        AddResourceConsumer(effects, "Refulgent Arrow", "Refulgent Arrow Ready");
        AddResourceConsumer(effects, "Shadowbite", "Shadowbite Ready");
        AddResourceConsumer(effects, "Resonant Arrow", "Resonant Arrow Ready");
        AddResourceConsumer(effects, "Radiant Encore", "Radiant Encore Ready");

        return new JobGaugeRules
        {
            Resources = resources,
            EffectByName = effects,
        };
    }

    private static JobGaugeRules BuildMachinistRules()
    {
        var effects = new Dictionary<string, IReadOnlyList<GaugeEffect>>(StringComparer.OrdinalIgnoreCase)
        {
            // ST combo — each hit gives +5 Heat; Clean Shot also gives +10 Battery
            ["Heated Split Shot"]  = E("Heat", +5),
            ["Heated Slug Shot"]   = E("Heat", +5),
            ["Heated Clean Shot"]  =
            [
                new GaugeEffect { GaugeName = "Heat",    Delta = +5  },
                new GaugeEffect { GaugeName = "Battery", Delta = +10 },
            ],
            // AoE combo
            ["Scattergun"]         = E("Heat", +10),
            ["Spread Shot"]        = E("Heat", +10),   // pre-82 name
            // Battery generators (CD GCDs)
            ["Hot Shot"]           = E("Battery", +20),
            ["Air Anchor"]         = E("Battery", +20),
            ["Chain Saw"]          = E("Battery", +20),
            ["Excavator"]          = E("Battery", +20),
            // Heat spender
            ["Hypercharge"]        = E("Heat", -50, minRequired: 50),
            // Battery spender (Queen summon)
            ["Automaton Queen"]    = E("Battery", -100, minRequired: 50),
            ["Rook Autoturret"]    = E("Battery", -100, minRequired: 50),
            ["Barrel Stabilizer"]  = E("Heat", +50),
        };

        return new JobGaugeRules
        {
            Resources =
            [
                new GaugeResource { Name = "Heat",    MaxValue = 100, InitialValue = 0, AvoidOvercap = true },
                new GaugeResource { Name = "Battery", MaxValue = 100, InitialValue = 0, AvoidOvercap = true },
            ],
            EffectByName = effects,
        };
    }

    // ── Pictomancer (PCT) — Palette Gauge, White Paint, Black Paint ────────
    //
    // Palette: 0–100, generated by Aetherhue spells (+25 each).
    //   Subtractive Palette costs 50 Palette, converts 1 White→Black Paint.
    // White Paint: 0–5, granted by Muse abilities.
    //   Holy in White costs 1 White Paint.
    // Black Paint: 0–1, granted by Subtractive Palette.
    //   Comet in Black costs 1 Black Paint.
    private static JobGaugeRules BuildDancerRules()
    {
        var effects = new Dictionary<string, IReadOnlyList<GaugeEffect>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Cascade"]         =
            [
                new GaugeEffect { GaugeName = "Esprit", Delta = +5 },
                new GaugeEffect { GaugeName = "Silken Symmetry", Delta = +1 },
            ],
            ["Fountain"]        =
            [
                new GaugeEffect { GaugeName = "Esprit", Delta = +5 },
                new GaugeEffect { GaugeName = "Silken Flow", Delta = +1 },
            ],
            ["Reverse Cascade"] =
            [
                new GaugeEffect { GaugeName = "Esprit", Delta = +5 },
                new GaugeEffect { GaugeName = "Silken Symmetry", Delta = -1, MinRequired = 1 },
            ],
            ["Fountainfall"]    =
            [
                new GaugeEffect { GaugeName = "Esprit", Delta = +5 },
                new GaugeEffect { GaugeName = "Silken Flow", Delta = -1, MinRequired = 1 },
            ],
            ["Windmill"]        =
            [
                new GaugeEffect { GaugeName = "Esprit", Delta = +5 },
                new GaugeEffect { GaugeName = "Silken Symmetry", Delta = +1 },
            ],
            ["Bladeshower"]     =
            [
                new GaugeEffect { GaugeName = "Esprit", Delta = +5 },
                new GaugeEffect { GaugeName = "Silken Flow", Delta = +1 },
            ],
            ["Rising Windmill"] =
            [
                new GaugeEffect { GaugeName = "Esprit", Delta = +5 },
                new GaugeEffect { GaugeName = "Silken Symmetry", Delta = -1, MinRequired = 1 },
            ],
            ["Bloodshower"]     =
            [
                new GaugeEffect { GaugeName = "Esprit", Delta = +5 },
                new GaugeEffect { GaugeName = "Silken Flow", Delta = -1, MinRequired = 1 },
            ],
            ["Saber Dance"]     = E("Esprit", -50, minRequired: 50),
            ["Fan Dance"]       = E("FourfoldFeather", -1, minRequired: 1),
            ["Fan Dance II"]    = E("FourfoldFeather", -1, minRequired: 1),
        };
        var resources = new List<GaugeResource>();

        EnsureResource(resources, "Esprit", 100, avoidOvercap: true);
        EnsureResource(resources, "FourfoldFeather", 4, avoidOvercap: true);
        EnsureResource(resources, "Standard Finish Ready", 1);
        EnsureResource(resources, "Technical Finish Ready", 1);
        EnsureResource(resources, "Finishing Move Ready", 1);
        EnsureResource(resources, "Flourishing Finish", 1);
        EnsureResource(resources, "Dance of the Dawn Ready", 1);
        EnsureResource(resources, "Starfall Dance Ready", 1);
        EnsureResource(resources, "Last Dance Ready", 1);
        EnsureResource(resources, "Improvised Finish Ready", 1);
        EnsureResource(resources, "Silken Symmetry", 1);
        EnsureResource(resources, "Silken Flow", 1);

        AddResourceGrant(effects, "Standard Step", "Standard Finish Ready");
        AddResourceConsumer(effects, "Standard Finish", "Standard Finish Ready");
        AddResourceConsumer(effects, "Double Standard Finish", "Standard Finish Ready");

        AddResourceGrant(effects, "Technical Step", "Technical Finish Ready");
        AppendEffects(effects, "Technical Finish",
            new GaugeEffect { GaugeName = "Technical Finish Ready", Delta = -1, MinRequired = 1 },
            new GaugeEffect { GaugeName = "Flourishing Finish", Delta = +1 },
            new GaugeEffect { GaugeName = "Dance of the Dawn Ready", Delta = +1 });
        AppendEffects(effects, "Quadruple Technical Finish",
            new GaugeEffect { GaugeName = "Technical Finish Ready", Delta = -1, MinRequired = 1 },
            new GaugeEffect { GaugeName = "Flourishing Finish", Delta = +1 },
            new GaugeEffect { GaugeName = "Dance of the Dawn Ready", Delta = +1 });

        AddResourceGrant(effects, "Flourish", "Finishing Move Ready");
        AddResourceGrant(effects, "Flourish", "Last Dance Ready");
        AddResourceGrant(effects, "Flourish", "Silken Symmetry");
        AddResourceGrant(effects, "Flourish", "Silken Flow");
        AppendEffects(effects, "Finishing Move",
            new GaugeEffect { GaugeName = "Finishing Move Ready", Delta = -1, MinRequired = 1 },
            new GaugeEffect { GaugeName = "Last Dance Ready", Delta = +1 });
        AddResourceGrant(effects, "Devilment", "Starfall Dance Ready");
        AddResourceGrant(effects, "Improvisation", "Improvised Finish Ready");
        AppendEffects(effects, "Double Standard Finish",
            new GaugeEffect { GaugeName = "Standard Finish Ready", Delta = -1, MinRequired = 1 },
            new GaugeEffect { GaugeName = "Last Dance Ready", Delta = +1 });
        AddResourceConsumer(effects, "Improvised Finish", "Improvised Finish Ready");
        AddResourceConsumer(effects, "Starfall Dance", "Starfall Dance Ready");
        AddResourceConsumer(effects, "Tillana", "Flourishing Finish");
        AddResourceConsumer(effects, "Dance of the Dawn", "Dance of the Dawn Ready");
        AddResourceConsumer(effects, "Last Dance", "Last Dance Ready");

        return new JobGaugeRules
        {
            Resources = resources,
            EffectByName = effects,
        };
    }

    private static JobGaugeRules BuildPictomancerRules()
    {
        var effects = new Dictionary<string, IReadOnlyList<GaugeEffect>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Fire in Red"] =
            [
                new GaugeEffect { GaugeName = "Aetherhues", Delta = +1 },
            ],
            ["Aero in Green"] =
            [
                new GaugeEffect { GaugeName = "Aetherhues", Delta = -1, MinRequired = 1 },
                new GaugeEffect { GaugeName = "Aetherhues II", Delta = +1 },
            ],
            ["Water in Blue"] =
            [
                new GaugeEffect { GaugeName = "Palette", Delta = +25 },
                new GaugeEffect { GaugeName = "WhitePaint", Delta = +1 },
                new GaugeEffect { GaugeName = "Aetherhues II", Delta = -1, MinRequired = 1 },
            ],
            ["Fire II in Red"] =
            [
                new GaugeEffect { GaugeName = "Aetherhues", Delta = +1 },
            ],
            ["Aero II in Green"] =
            [
                new GaugeEffect { GaugeName = "Aetherhues", Delta = -1, MinRequired = 1 },
                new GaugeEffect { GaugeName = "Aetherhues II", Delta = +1 },
            ],
            ["Water II in Blue"] =
            [
                new GaugeEffect { GaugeName = "Palette", Delta = +25 },
                new GaugeEffect { GaugeName = "WhitePaint", Delta = +1 },
                new GaugeEffect { GaugeName = "Aetherhues II", Delta = -1, MinRequired = 1 },
            ],
            ["Blizzard in Cyan"] =
            [
                new GaugeEffect { GaugeName = "Subtractive Palette", Delta = -1, MinRequired = 1 },
                new GaugeEffect { GaugeName = "Aetherhues", Delta = +1 },
            ],
            ["Stone in Yellow"] =
            [
                new GaugeEffect { GaugeName = "Subtractive Palette", Delta = -1, MinRequired = 1 },
                new GaugeEffect { GaugeName = "Aetherhues", Delta = -1, MinRequired = 1 },
                new GaugeEffect { GaugeName = "Aetherhues II", Delta = +1 },
            ],
            ["Thunder in Magenta"] =
            [
                new GaugeEffect { GaugeName = "Subtractive Palette", Delta = -1, MinRequired = 1 },
                new GaugeEffect { GaugeName = "Aetherhues II", Delta = -1, MinRequired = 1 },
                new GaugeEffect { GaugeName = "WhitePaint", Delta = +1 },
            ],
            ["Blizzard II in Cyan"] =
            [
                new GaugeEffect { GaugeName = "Subtractive Palette", Delta = -1, MinRequired = 1 },
                new GaugeEffect { GaugeName = "Aetherhues", Delta = +1 },
            ],
            ["Stone II in Yellow"] =
            [
                new GaugeEffect { GaugeName = "Subtractive Palette", Delta = -1, MinRequired = 1 },
                new GaugeEffect { GaugeName = "Aetherhues", Delta = -1, MinRequired = 1 },
                new GaugeEffect { GaugeName = "Aetherhues II", Delta = +1 },
            ],
            ["Thunder II in Magenta"] =
            [
                new GaugeEffect { GaugeName = "Subtractive Palette", Delta = -1, MinRequired = 1 },
                new GaugeEffect { GaugeName = "Aetherhues II", Delta = -1, MinRequired = 1 },
                new GaugeEffect { GaugeName = "WhitePaint", Delta = +1 },
            ],
            ["Rainbow Drip"] =
            [
                new GaugeEffect { GaugeName = "Rainbow Bright", Delta = -1, MinRequired = 1 },
                new GaugeEffect { GaugeName = "WhitePaint", Delta = +1 },
            ],
            // Subtractive Palette — costs 50 Palette, -1 White Paint → +1 Black Paint
            ["Subtractive Palette"] =
            [
                new GaugeEffect { GaugeName = "Palette",    Delta = -50, MinRequired = 50 },
                new GaugeEffect { GaugeName = "WhitePaint", Delta = -1, MinRequired = 1 },
                new GaugeEffect { GaugeName = "BlackPaint", Delta = +1 },
                new GaugeEffect { GaugeName = "Subtractive Palette", Delta = +3 },
                new GaugeEffect { GaugeName = "Monochrome Tones", Delta = +1 },
            ],
            // Holy in White — costs 1 White Paint
            ["Holy in White"]  =
            [
                new GaugeEffect { GaugeName = "WhitePaint", Delta = -1, MinRequired = 1 },
            ],
            ["Holy II in White"]=
            [
                new GaugeEffect { GaugeName = "WhitePaint", Delta = -1, MinRequired = 1 },
            ],
            // Comet in Black — costs 1 Black Paint
            ["Comet in Black"] =
            [
                new GaugeEffect { GaugeName = "BlackPaint", Delta = -1, MinRequired = 1 },
                new GaugeEffect { GaugeName = "Monochrome Tones", Delta = -1, MinRequired = 1 },
            ],
            ["Starry Muse"] =
            [
                new GaugeEffect { GaugeName = "Subtractive Spectrum", Delta = +1 },
                new GaugeEffect { GaugeName = "Inspiration", Delta = +1 },
                new GaugeEffect { GaugeName = "Hyperphantasia", Delta = +5 },
                new GaugeEffect { GaugeName = "Starstruck", Delta = +1 },
            ],
            ["Hammer Motif"] = E("Hammer Time", +3),
            ["Hammer Stamp"] = E("Hammer Time", -1, minRequired: 1),
            ["Hammer Brush"] = E("Hammer Time", -1, minRequired: 1),
            ["Polishing Hammer"] = E("Hammer Time", -1, minRequired: 1),
            ["Pom Muse"] =
            [
                new GaugeEffect { GaugeName = "Pom Motif", Delta = -1, MinRequired = 1 },
                new GaugeEffect { GaugeName = "Wing Sketch", Delta = +1 },
            ],
            ["Winged Muse"] =
            [
                new GaugeEffect { GaugeName = "Wing Motif", Delta = -1, MinRequired = 1 },
                new GaugeEffect { GaugeName = "Moogle Portrait", Delta = +1 },
                new GaugeEffect { GaugeName = "Claw Sketch", Delta = +1 },
            ],
            ["Clawed Muse"] =
            [
                new GaugeEffect { GaugeName = "Claw Motif", Delta = -1, MinRequired = 1 },
                new GaugeEffect { GaugeName = "Maw Sketch", Delta = +1 },
            ],
            ["Fanged Muse"] =
            [
                new GaugeEffect { GaugeName = "Maw Motif", Delta = -1, MinRequired = 1 },
                new GaugeEffect { GaugeName = "Madeen Portrait", Delta = +1 },
                new GaugeEffect { GaugeName = "Pom Sketch", Delta = +1 },
            ],
            ["Mog of the Ages"] = E("Moogle Portrait", -1, minRequired: 1),
            ["Retribution of the Madeen"] = E("Madeen Portrait", -1, minRequired: 1),
            ["Star Prism"] = E("Starstruck", -1, minRequired: 1),
            ["Pom Motif"] = E("Pom Sketch", -1, minRequired: 1),
            ["Wing Motif"] = E("Wing Sketch", -1, minRequired: 1),
            ["Claw Motif"] = E("Claw Sketch", -1, minRequired: 1),
            ["Maw Motif"] = E("Maw Sketch", -1, minRequired: 1),
            ["Tempera Coat"] = E("Tempera Coat", +1),
            ["Tempera Grassa"] = E("Tempera Coat", -1, minRequired: 1),
        };

        var resources = new List<GaugeResource>
        {
            new() { Name = "Palette", MaxValue = 100, InitialValue = 0, AvoidOvercap = true },
            new() { Name = "WhitePaint", MaxValue = 5, InitialValue = 0, AvoidOvercap = true },
            new() { Name = "BlackPaint", MaxValue = 1, InitialValue = 0, AvoidOvercap = true },
            new() { Name = "Aetherhues", MaxValue = 1, InitialValue = 0 },
            new() { Name = "Aetherhues II", MaxValue = 1, InitialValue = 0 },
            new() { Name = "Subtractive Palette", MaxValue = 3, InitialValue = 0 },
            new() { Name = "Monochrome Tones", MaxValue = 1, InitialValue = 0 },
            new() { Name = "Subtractive Spectrum", MaxValue = 1, InitialValue = 0 },
            new() { Name = "Inspiration", MaxValue = 1, InitialValue = 0 },
            new() { Name = "Hyperphantasia", MaxValue = 5, InitialValue = 0 },
            new() { Name = "Rainbow Bright", MaxValue = 1, InitialValue = 0 },
            new() { Name = "Starstruck", MaxValue = 1, InitialValue = 0 },
            new() { Name = "Hammer Time", MaxValue = 3, InitialValue = 0 },
            new() { Name = "Pom Sketch", MaxValue = 1, InitialValue = 0 },
            new() { Name = "Wing Sketch", MaxValue = 1, InitialValue = 0 },
            new() { Name = "Claw Sketch", MaxValue = 1, InitialValue = 0 },
            new() { Name = "Maw Sketch", MaxValue = 1, InitialValue = 0 },
            new() { Name = "Pom Motif", MaxValue = 1, InitialValue = 0 },
            new() { Name = "Wing Motif", MaxValue = 1, InitialValue = 0 },
            new() { Name = "Claw Motif", MaxValue = 1, InitialValue = 0 },
            new() { Name = "Maw Motif", MaxValue = 1, InitialValue = 0 },
            new() { Name = "Moogle Portrait", MaxValue = 1, InitialValue = 0 },
            new() { Name = "Madeen Portrait", MaxValue = 1, InitialValue = 0 },
            new() { Name = "Tempera Coat", MaxValue = 1, InitialValue = 0 },
        };
        var instantCastRules = new List<InstantCastRule>();
        AddSwiftcast(resources, effects, instantCastRules);

        return new JobGaugeRules
        {
            Resources = resources,
            EffectByName = effects,
            InstantCastRules = instantCastRules,
        };
    }

    // ── Viper (VPR) — Serpent Offerings + Rattling Coil ────────────────────
    //
    // Serpent Offerings: 0–100.
    //   Combo enders generate +10 each.
    //   Reawaken costs 50 and grants 3 Rattling Coil.
    // Rattling Coil: 0–3, granted by Reawaken.
    //   Uncoiled Fury / Uncoiled Twinblood each cost 1.
    private static JobGaugeRules BuildViperRules()
    {
        var effects = new Dictionary<string, IReadOnlyList<GaugeEffect>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Hunter's Sting"]    = E("SerpentOfferings", 0),
            ["Swiftskin's Sting"] = E("SerpentOfferings", 0),
            ["Flanksting Strike"] = E("SerpentOfferings", +10),
            ["Hindsting Strike"]  = E("SerpentOfferings", +10),
            ["Flanksbane Fang"]   = E("SerpentOfferings", +10),
            ["Hindsbane Fang"]    = E("SerpentOfferings", +10),
            ["Jagged Maw"]        = E("SerpentOfferings", +10),
            ["Bloodied Maw"]      = E("SerpentOfferings", +10),
            ["Hunter's Coil"]     = E("SerpentOfferings", +5),
            ["Swiftskin's Coil"]  = E("SerpentOfferings", +5),
            ["Hunter's Den"]      = E("SerpentOfferings", +5),
            ["Swiftskin's Den"]   = E("SerpentOfferings", +5),
            ["Vicewinder"]        = E("RattlingCoil", +1),
            ["Vicepit"]           = E("RattlingCoil", +1),
            ["Reawaken"]          = E("SerpentOfferings", -50, minRequired: 50),
            // Reawaken sequence abilities — no cost
            ["First Generation"]  = E("SerpentOfferings", 0),
            ["Second Generation"] = E("SerpentOfferings", 0),
            ["Third Generation"]  = E("SerpentOfferings", 0),
            ["Fourth Generation"] = E("SerpentOfferings", 0),
            ["Ouroboros"]         = E("SerpentOfferings", 0),
            // Rattling Coil spenders
            ["Uncoiled Fury"]       = E("RattlingCoil", -1, minRequired: 1),
            ["Uncoiled Twinblood"]  = E("RattlingCoil", -1, minRequired: 1),
        };

        return new JobGaugeRules
        {
            Resources =
            [
                new GaugeResource { Name = "SerpentOfferings", MaxValue = 100, InitialValue = 0, AvoidOvercap = true },
                new GaugeResource { Name = "RattlingCoil",     MaxValue = 3,   InitialValue = 0, AvoidOvercap = true },
            ],
            EffectByName = effects,
        };
    }

    // ── Red Mage (RDM) — Black Mana + White Mana ───────────────────────────
    //
    // Both gauges: 0–100, start at 0.
    // Melee combo requires both ≥50; each hit costs both.
    // Note: imbalance bonus (+4 to lower mana) is not simulated — values are approximate.
    private static JobGaugeRules BuildRedMageRules()
    {
        var effects = new Dictionary<string, IReadOnlyList<GaugeEffect>>(StringComparer.OrdinalIgnoreCase);
        var resources = new List<GaugeResource>
        {
            new() { Name = "BlackMana", MaxValue = 100, InitialValue = 0, AvoidOvercap = true },
            new() { Name = "WhiteMana", MaxValue = 100, InitialValue = 0, AvoidOvercap = true },
            new() { Name = "ManaStack", MaxValue = 3, InitialValue = 0, AvoidOvercap = true },
            new() { Name = "Dualcast", MaxValue = 1, InitialValue = 0 },
            new() { Name = "Verfire Ready", MaxValue = 1, InitialValue = 0 },
            new() { Name = "Verstone Ready", MaxValue = 1, InitialValue = 0 },
            new() { Name = "Grand Impact Ready", MaxValue = 1, InitialValue = 0 },
            new() { Name = "Prefulgence Ready", MaxValue = 1, InitialValue = 0 },
            new() { Name = "Vice of Thorns Ready", MaxValue = 1, InitialValue = 0 },
        };
        var instantCastRules = new List<InstantCastRule>();
        var hardcastGrantRules = new List<HardcastGrantRule>
        {
            new()
            {
                ResourceName = "Dualcast",
                AppliesToAnyCastTimeSpell = true,
            },
        };

        AddSwiftcast(resources, effects, instantCastRules);
        instantCastRules.Add(new InstantCastRule
        {
            ResourceName = "Dualcast",
            AppliesToAnyCastTimeSpell = true,
        });

        // Helpers for common patterns
        void BW(string name, int b, int w, int bMin = 0, int wMin = 0) =>
            effects[name] =
            [
                new GaugeEffect { GaugeName = "BlackMana", Delta = b, MinRequired = bMin },
                new GaugeEffect { GaugeName = "WhiteMana", Delta = w, MinRequired = wMin },
            ];

        void B(string name, int delta) =>
            effects[name] = [new GaugeEffect { GaugeName = "BlackMana", Delta = delta }];

        void W(string name, int delta) =>
            effects[name] = [new GaugeEffect { GaugeName = "WhiteMana", Delta = delta }];

        // Balanced generators
        BW("Jolt",       +2, +2);
        BW("Jolt II",    +2, +2);
        BW("Jolt III",   +2, +2);
        BW("Impact",     +2, +2);
        BW("Grand Impact",  +3, +3);
        BW("Scorch",     +4, +4);
        BW("Resolution", +4, +4);
        // Thunder / Aero
        effects["Verthunder"] =
        [
            new GaugeEffect { GaugeName = "BlackMana", Delta = +6 },
            new GaugeEffect { GaugeName = "Verfire Ready", Delta = +1 },
        ];
        effects["Verthunder II"] =
        [
            new GaugeEffect { GaugeName = "BlackMana", Delta = +7 },
            new GaugeEffect { GaugeName = "Verfire Ready", Delta = +1 },
        ];
        effects["Verthunder III"] =
        [
            new GaugeEffect { GaugeName = "BlackMana", Delta = +6 },
            new GaugeEffect { GaugeName = "Verfire Ready", Delta = +1 },
        ];
        effects["Veraero"] =
        [
            new GaugeEffect { GaugeName = "WhiteMana", Delta = +6 },
            new GaugeEffect { GaugeName = "Verstone Ready", Delta = +1 },
        ];
        effects["Veraero II"] =
        [
            new GaugeEffect { GaugeName = "WhiteMana", Delta = +7 },
            new GaugeEffect { GaugeName = "Verstone Ready", Delta = +1 },
        ];
        effects["Veraero III"] =
        [
            new GaugeEffect { GaugeName = "WhiteMana", Delta = +6 },
            new GaugeEffect { GaugeName = "Verstone Ready", Delta = +1 },
        ];
        // Procs
        effects["Verfire"] =
        [
            new GaugeEffect { GaugeName = "BlackMana", Delta = +5 },
            new GaugeEffect { GaugeName = "Verfire Ready", Delta = -1, MinRequired = 1 },
        ];
        effects["Verstone"] =
        [
            new GaugeEffect { GaugeName = "WhiteMana", Delta = +5 },
            new GaugeEffect { GaugeName = "Verstone Ready", Delta = -1, MinRequired = 1 },
        ];
        effects["Acceleration"] = E("Grand Impact Ready", +1);
        effects["Manafication"] = E("Prefulgence Ready", +1);
        effects["Embolden"] = E("Vice of Thorns Ready", +1);
        effects["Grand Impact"] =
        [
            new GaugeEffect { GaugeName = "BlackMana", Delta = +3 },
            new GaugeEffect { GaugeName = "WhiteMana", Delta = +3 },
            new GaugeEffect { GaugeName = "Grand Impact Ready", Delta = -1, MinRequired = 1 },
        ];
        effects["Prefulgence"] =
        [
            new GaugeEffect { GaugeName = "Prefulgence Ready", Delta = -1, MinRequired = 1 },
        ];
        effects["Vice of Thorns"] =
        [
            new GaugeEffect { GaugeName = "Vice of Thorns Ready", Delta = -1, MinRequired = 1 },
        ];
        effects["Enchanted Riposte"] =
        [
            new GaugeEffect { GaugeName = "BlackMana", Delta = -20, MinRequired = 50 },
            new GaugeEffect { GaugeName = "WhiteMana", Delta = -20, MinRequired = 50 },
            new GaugeEffect { GaugeName = "ManaStack", Delta = +1 },
        ];
        effects["Enchanted Zwerchhau"] =
        [
            new GaugeEffect { GaugeName = "BlackMana", Delta = -15 },
            new GaugeEffect { GaugeName = "WhiteMana", Delta = -15 },
            new GaugeEffect { GaugeName = "ManaStack", Delta = +1 },
        ];
        effects["Enchanted Redoublement"] =
        [
            new GaugeEffect { GaugeName = "BlackMana", Delta = -15 },
            new GaugeEffect { GaugeName = "WhiteMana", Delta = -15 },
            new GaugeEffect { GaugeName = "ManaStack", Delta = +1 },
        ];
        effects["Enchanted Moulinet"] =
        [
            new GaugeEffect { GaugeName = "BlackMana", Delta = -20, MinRequired = 50 },
            new GaugeEffect { GaugeName = "WhiteMana", Delta = -20, MinRequired = 50 },
            new GaugeEffect { GaugeName = "ManaStack", Delta = +1 },
        ];
        effects["Enchanted Moulinet Deux"] =
        [
            new GaugeEffect { GaugeName = "BlackMana", Delta = -15 },
            new GaugeEffect { GaugeName = "WhiteMana", Delta = -15 },
            new GaugeEffect { GaugeName = "ManaStack", Delta = +1 },
        ];
        effects["Enchanted Moulinet Trois"] =
        [
            new GaugeEffect { GaugeName = "BlackMana", Delta = -15 },
            new GaugeEffect { GaugeName = "WhiteMana", Delta = -15 },
            new GaugeEffect { GaugeName = "ManaStack", Delta = +1 },
        ];
        effects["Enchanted Reprise"] =
        [
            new GaugeEffect { GaugeName = "BlackMana", Delta = -5, MinRequired = 5 },
            new GaugeEffect { GaugeName = "WhiteMana", Delta = -5, MinRequired = 5 },
        ];
        effects["Verholy"] =
        [
            new GaugeEffect { GaugeName = "WhiteMana", Delta = +11 },
            new GaugeEffect { GaugeName = "ManaStack", Delta = -3, MinRequired = 3 },
        ];
        effects["Verflare"] =
        [
            new GaugeEffect { GaugeName = "BlackMana", Delta = +11 },
            new GaugeEffect { GaugeName = "ManaStack", Delta = -3, MinRequired = 3 },
        ];

        return new JobGaugeRules
        {
            Resources = resources,
            EffectByName = effects,
            InstantCastRules = instantCastRules,
            HardcastGrantRules = hardcastGrantRules,
        };
    }

    // ── Scholar (SCH) — Aetherflow ─────────────────────────────────────────
    //
    // Aetherflow: 0–3, starts at 0. Aetherflow ability resets to 3.
    //   Spenders: Energy Drain/Siphon/Lustrate/Indomitability/Excogitation/Sacred Soil (−1 each)
    private static JobGaugeRules BuildSummonerRules()
    {
        var effects = new Dictionary<string, IReadOnlyList<GaugeEffect>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Energy Drain"] =
            [
                new GaugeEffect { GaugeName = "Aetherflow", Delta = +2 },
                new GaugeEffect { GaugeName = "Ruin IV Ready", Delta = +1 },
            ],
            ["Energy Siphon"] =
            [
                new GaugeEffect { GaugeName = "Aetherflow", Delta = +2 },
                new GaugeEffect { GaugeName = "Ruin IV Ready", Delta = +1 },
            ],
            ["Ruin IV"] = E("Ruin IV Ready", -1, minRequired: 1),
            ["Fester"] = E("Aetherflow", -1, minRequired: 1),
            ["Painflare"] = E("Aetherflow", -1, minRequired: 1),
            ["Searing Light"] = E("Searing Flash Ready", +1),
            ["Searing Flash"] = E("Searing Flash Ready", -1, minRequired: 1),
            ["Summon Ifrit"] = E("Crimson Cyclone Ready", +1),
            ["Summon Ifrit II"] = E("Crimson Cyclone Ready", +1),
            ["Crimson Cyclone"] =
            [
                new GaugeEffect { GaugeName = "Crimson Cyclone Ready", Delta = -1, MinRequired = 1 },
                new GaugeEffect { GaugeName = "Crimson Strike Ready", Delta = +1 },
            ],
            ["Crimson Strike"] = E("Crimson Strike Ready", -1, minRequired: 1),
            ["Summon Titan"] = E("Mountain Buster Ready", +4),
            ["Summon Titan II"] = E("Mountain Buster Ready", +4),
            ["Mountain Buster"] = E("Mountain Buster Ready", -1, minRequired: 1),
            ["Summon Garuda"] = E("Slipstream Ready", +1),
            ["Summon Garuda II"] = E("Slipstream Ready", +1),
            ["Slipstream"] = E("Slipstream Ready", -1, minRequired: 1),
        };
        var resources = new List<GaugeResource>
        {
            new() { Name = "Aetherflow", MaxValue = 2, InitialValue = 0, AvoidOvercap = true },
            new() { Name = "Ruin IV Ready", MaxValue = 1, InitialValue = 0 },
            new() { Name = "Searing Flash Ready", MaxValue = 1, InitialValue = 0 },
            new() { Name = "Crimson Cyclone Ready", MaxValue = 1, InitialValue = 0 },
            new() { Name = "Crimson Strike Ready", MaxValue = 1, InitialValue = 0 },
            new() { Name = "Mountain Buster Ready", MaxValue = 4, InitialValue = 0 },
            new() { Name = "Slipstream Ready", MaxValue = 1, InitialValue = 0 },
        };
        var instantCastRules = new List<InstantCastRule>();
        AddSwiftcast(resources, effects, instantCastRules);

        return new JobGaugeRules
        {
            Resources = resources,
            EffectByName = effects,
            InstantCastRules = instantCastRules,
        };
    }

    private static JobGaugeRules BuildScholarRules()
    {
        var effects = new Dictionary<string, IReadOnlyList<GaugeEffect>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Aetherflow"]       =
            [
                new GaugeEffect { GaugeName = "Aetherflow", SetValue = 3, MaxAllowedBeforeUse = 0 },
            ],
            ["Dissipation"]      =
            [
                new GaugeEffect { GaugeName = "Aetherflow", SetValue = 3, MaxAllowedBeforeUse = 0 },
            ],
            ["Energy Drain"]     =
            [
                new GaugeEffect { GaugeName = "Aetherflow", Delta = -1, MinRequired = 1 },
                new GaugeEffect { GaugeName = "FaeAether", Delta = +10 },
            ],
            ["Energy Siphon"]    =
            [
                new GaugeEffect { GaugeName = "Aetherflow", Delta = -1, MinRequired = 1 },
                new GaugeEffect { GaugeName = "FaeAether", Delta = +10 },
            ],
            ["Lustrate"]         =
            [
                new GaugeEffect { GaugeName = "Aetherflow", Delta = -1, MinRequired = 1 },
                new GaugeEffect { GaugeName = "FaeAether", Delta = +10 },
            ],
            ["Indomitability"]   =
            [
                new GaugeEffect { GaugeName = "Aetherflow", Delta = -1, MinRequired = 1 },
                new GaugeEffect { GaugeName = "FaeAether", Delta = +10 },
            ],
            ["Excogitation"]     =
            [
                new GaugeEffect { GaugeName = "Aetherflow", Delta = -1, MinRequired = 1 },
                new GaugeEffect { GaugeName = "FaeAether", Delta = +10 },
            ],
            ["Sacred Soil"]      =
            [
                new GaugeEffect { GaugeName = "Aetherflow", Delta = -1, MinRequired = 1 },
                new GaugeEffect { GaugeName = "FaeAether", Delta = +10 },
            ],
            ["Aetherpact"]       = E("FaeAether", -10, minRequired: 10),
            ["Chain Stratagem"]  = E("Baneful Impaction Ready", +1),
            ["Baneful Impaction"]= E("Baneful Impaction Ready", -1, minRequired: 1),
            ["Summon Seraph"]    = E("Consolation Ready", +2),
            ["Consolation"]      = E("Consolation Ready", -1, minRequired: 1),
        };

        var resources = new List<GaugeResource>
        {
            new() { Name = "Aetherflow", MaxValue = 3, InitialValue = 0, AvoidOvercap = true },
            new() { Name = "FaeAether", MaxValue = 100, InitialValue = 0, AvoidOvercap = true },
            new() { Name = "Baneful Impaction Ready", MaxValue = 1, InitialValue = 0 },
            new() { Name = "Consolation Ready", MaxValue = 2, InitialValue = 0 },
        };
        var instantCastRules = new List<InstantCastRule>();
        AddSwiftcast(resources, effects, instantCastRules);

        return new JobGaugeRules
        {
            Resources    = resources,
            EffectByName = effects,
            InstantCastRules = instantCastRules,
        };
    }

    // ── White Mage (WHM) — Lily + Blood Lily ───────────────────────────────
    //
    // Lily: 0–3, gains +1 passively every 20 s.
    //   Afflatus Solace/Rapture each cost 1 Lily and grant 1 Blood Lily.
    // Blood Lily: 0–3, filled by spending Lilies.
    //   Afflatus Misery costs 3 Blood Lily.
    private static JobGaugeRules BuildWhiteMageRules()
    {
        var effects = new Dictionary<string, IReadOnlyList<GaugeEffect>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Afflatus Solace"] =
            [
                new GaugeEffect { GaugeName = "Lily",      Delta = -1, MinRequired = 1 },
                new GaugeEffect { GaugeName = "BloodLily", Delta = +1 },
            ],
            ["Afflatus Rapture"] =
            [
                new GaugeEffect { GaugeName = "Lily",      Delta = -1, MinRequired = 1 },
                new GaugeEffect { GaugeName = "BloodLily", Delta = +1 },
            ],
            ["Afflatus Misery"] =
            [
                new GaugeEffect { GaugeName = "BloodLily", Delta = -3, MinRequired = 3 },
            ],
        };

        AppendEffects(effects, "Presence of Mind",
            new GaugeEffect { GaugeName = "Sacred Sight", Delta = +3 });
        AppendEffects(effects, "Glare IV",
            new GaugeEffect { GaugeName = "Sacred Sight", Delta = -1, MinRequired = 1 });
        AppendEffects(effects, "Divine Benison",
            new GaugeEffect { GaugeName = "Divine Grace", Delta = +1 });
        AppendEffects(effects, "Divine Caress",
            new GaugeEffect { GaugeName = "Divine Grace", Delta = -1, MinRequired = 1 });

        var resources = new List<GaugeResource>
        {
            new()
            {
                Name = "Lily", MaxValue = 3, InitialValue = 0,
                AvoidOvercap = true,
                PassiveGenerationIntervalSec = 20,
            },
            new() { Name = "BloodLily", MaxValue = 3, InitialValue = 0, AvoidOvercap = true },
            new() { Name = "Sacred Sight", MaxValue = 3, InitialValue = 0 },
            new() { Name = "Divine Grace", MaxValue = 1, InitialValue = 0 },
        };
        var instantCastRules = new List<InstantCastRule>();
        AddSwiftcast(resources, effects, instantCastRules);

        return new JobGaugeRules
        {
            Resources = resources,
            EffectByName = effects,
            InstantCastRules = instantCastRules,
        };
    }

    // ── Sage (SGE) — Addersgall ────────────────────────────────────────────
    //
    // Addersgall: 0–3, gains +1 passively every 20 s.
    //   Rhizomata: CD that restores +1.
    //   Spenders: Druochole / Kerachole / Ixochole / Taurochole (−1 each, need ≥1)
    //
    // Addersting excluded — generated when Eukrasia Diagnosis shield is absorbed,
    // which is not observable from FFLogs cast data.
    private static JobGaugeRules BuildSageRules()
    {
        var effects = new Dictionary<string, IReadOnlyList<GaugeEffect>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Rhizomata"]  = E("Addersgall", +1),
            ["Druochole"]  = E("Addersgall", -1, minRequired: 1),
            ["Kerachole"]  = E("Addersgall", -1, minRequired: 1),
            ["Ixochole"]   = E("Addersgall", -1, minRequired: 1),
            ["Taurochole"] = E("Addersgall", -1, minRequired: 1),
        };

        AppendEffects(effects, "Eukrasia",
            new GaugeEffect { GaugeName = "Eukrasia", Delta = +1 });
        AppendEffects(effects, "Eukrasian Dosis",
            new GaugeEffect { GaugeName = "Eukrasia", Delta = -1, MinRequired = 1 });
        AppendEffects(effects, "Eukrasian Dosis II",
            new GaugeEffect { GaugeName = "Eukrasia", Delta = -1, MinRequired = 1 });
        AppendEffects(effects, "Eukrasian Dosis III",
            new GaugeEffect { GaugeName = "Eukrasia", Delta = -1, MinRequired = 1 });
        AppendEffects(effects, "Eukrasian Diagnosis",
            new GaugeEffect { GaugeName = "Eukrasia", Delta = -1, MinRequired = 1 });
        AppendEffects(effects, "Eukrasian Prognosis",
            new GaugeEffect { GaugeName = "Eukrasia", Delta = -1, MinRequired = 1 });

        var resources = new List<GaugeResource>
        {
            new()
            {
                Name = "Addersgall", MaxValue = 3, InitialValue = 0,
                AvoidOvercap = true,
                PassiveGenerationIntervalSec = 30,
            },
            new() { Name = "Addersting", MaxValue = 3, InitialValue = 0, AvoidOvercap = true },
            new() { Name = "Eukrasia", MaxValue = 1, InitialValue = 0 },
        };
        effects["Toxikon"] = E("Addersting", -1, minRequired: 1);
        effects["Toxikon II"] = E("Addersting", -1, minRequired: 1);
        var instantCastRules = new List<InstantCastRule>();
        AddSwiftcast(resources, effects, instantCastRules);

        return new JobGaugeRules
        {
            Resources = resources,
            EffectByName = effects,
            InstantCastRules = instantCastRules,
        };
    }

    // ── Black Mage (BLM) — Polyglot ────────────────────────────────────────
    //
    // Polyglot: 0–3.  Passively gains +1 every 30 s while in Astral Fire or Umbral Ice
    // (assumed to be active throughout combat).  Amplifier (CD) grants an extra +1.
    //   Spenders: Xenoglossy / Foul (need ≥1, cost 1)
    private static JobGaugeRules BuildBlackMageRules()
    {
        var effects = new Dictionary<string, IReadOnlyList<GaugeEffect>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Amplifier"]  = E("Polyglot", +1),
            ["Xenoglossy"] = E("Polyglot", -1, minRequired: 1),
            ["Foul"]       = E("Polyglot", -1, minRequired: 1),
            ["Fire IV"]    = E("AstralSoul", +1),
            ["Flare"]      = E("AstralSoul", +1),
            ["Flare Star"] = E("AstralSoul", -6, minRequired: 6),
            ["Triplecast"] = E("Triplecast", +3),
        };

        var resources = new List<GaugeResource>
        {
            new()
            {
                Name = "Polyglot", MaxValue = 3, InitialValue = 0,
                AvoidOvercap = true,
                PassiveGenerationIntervalSec = 30,
            },
            new() { Name = "AstralSoul", MaxValue = 6, InitialValue = 0, AvoidOvercap = true },
            new() { Name = "Triplecast", MaxValue = 3, InitialValue = 0 },
        };
        var instantCastRules = new List<InstantCastRule>();
        AddSwiftcast(resources, effects, instantCastRules);
        instantCastRules.Add(new InstantCastRule
        {
            ResourceName = "Triplecast",
            AppliesToAnyCastTimeSpell = true,
        });

        return new JobGaugeRules
        {
            Resources = resources,
            EffectByName = effects,
            InstantCastRules = instantCastRules,
        };
    }

    // ── Astrologian (AST) — Card Draw State ────────────────────────────────
    //
    // No numeric gauge. AST alternates between Astral Draw and Umbral Draw;
    // each draw unlocks a distinct set of 4 independently playable cards until
    // the next draw swaps the active hand.
    //
    // Astral Draw  → Balance, Arrow, Spire, Lord of Crowns
    // Umbral Draw  → Spear, Ewer, Bole,  Lady of Crowns
    private static JobGaugeRules BuildAstrologianRules()
    {
        var astralCards = new[]
        {
            "The Balance",
            "The Arrow",
            "The Spire",
            "Lord of Crowns",
        };
        var umbralCards = new[]
        {
            "The Spear",
            "The Ewer",
            "The Bole",
            "Lady of Crowns",
        };

        var effects = new Dictionary<string, IReadOnlyList<GaugeEffect>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Divination"] = E("Divining", +1),
            ["Oracle"] = E("Divining", -1, minRequired: 1),
        };
        var resources = new List<GaugeResource>
        {
            new() { Name = "Divining", MaxValue = 1, InitialValue = 0 },
        };
        var instantCastRules = new List<InstantCastRule>();
        var repeatableGrantedActionRules = new List<RepeatableGrantedActionRule>();
        AddSwiftcast(resources, effects, instantCastRules);
        AddGroupedGrantedActions(resources, effects, "Astral Draw", astralCards, umbralCards);
        AddGroupedGrantedActions(resources, effects, "Umbral Draw", umbralCards, astralCards);
        AddGrantedAction(resources, effects, "Divination", "Oracle");
        AddGrantedAction(resources, effects, "Neutral Sect", "Sun Sign");
        AddGrantedAction(resources, effects, "Earthly Star", "Stellar Detonation");
        AddGrantedAction(resources, effects, "Macrocosmos", "Microcosmos");
        AddRepeatableGrantedAction(
            resources,
            repeatableGrantedActionRules,
            "Horoscope",
            "Horoscope Ready",
            1,
            "Horoscope",
            "Horoscope Helios");

        return new JobGaugeRules
        {
            Resources = resources,
            EffectByName = effects,
            CardDraw = new CardDrawRules
            {
                AstralDrawName = "Astral Draw",
                UmbralDrawName = "Umbral Draw",
                AstralCards = new HashSet<string>(astralCards, StringComparer.OrdinalIgnoreCase),
                UmbralCards = new HashSet<string>(umbralCards, StringComparer.OrdinalIgnoreCase),
            },
            InstantCastRules = instantCastRules,
            RepeatableGrantedActionRules = repeatableGrantedActionRules,
        };
    }
}
