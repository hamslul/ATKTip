using System;
using System.Collections.Generic;
using System.Linq;

namespace ATKTip.Data;

public sealed class ActionStateDatabase
{
    public sealed class ActionStateEffect
    {
        public string StateName { get; init; } = string.Empty;
        public int MinRequired { get; init; }
        public int Delta { get; init; }
        public int MaxValue { get; init; } = 1;
    }

    public sealed class ActionStateRule
    {
        public IReadOnlyList<ActionStateEffect> Effects { get; init; } = [];
    }

    private static readonly string[] ConsumableStateTokens =
    [
        " ready",
        "ready",
        "dualcast",
        "hawk's eye",
        "flourishing ",
        "starstruck",
        "divine might",
        "aetherhues",
        "aetherhues ii",
        "hyperphantasia",
        "monochrome tones",
        "subtractive palette",
        "rainbow bright",
        "hammer time",
        "tempera coat",
        "divining",
    ];

    private static readonly string[] SpecNames =
    [
        "Paladin",
        "Warrior",
        "Dark Knight",
        "Gunbreaker",
        "White Mage",
        "Scholar",
        "Astrologian",
        "Sage",
        "Monk",
        "Dragoon",
        "Ninja",
        "Samurai",
        "Reaper",
        "Viper",
        "Bard",
        "Machinist",
        "Dancer",
        "Black Mage",
        "Summoner",
        "Red Mage",
        "Pictomancer",
    ];

    private readonly Dictionary<uint, ActionStateRule> byId = [];
    private readonly Dictionary<string, ActionStateRule> byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> grantorsByState = new(StringComparer.OrdinalIgnoreCase);

    public ActionStateDatabase(RecastDatabase recastDatabase)
    {
        var actionsByName = recastDatabase.GetAllActions()
            .Where(action => action.IsPlayerAction)
            .GroupBy(action => action.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(action => action.AbilityId).First(),
                StringComparer.OrdinalIgnoreCase);

        var effectsById = new Dictionary<uint, List<ActionStateEffect>>();
        var effectsByName = new Dictionary<string, List<ActionStateEffect>>(StringComparer.OrdinalIgnoreCase);

        foreach (var action in recastDatabase.GetAllActions().Where(action => action.IsPlayerAction))
            ImportSheetStatuses(action, effectsById, effectsByName);

        foreach (var specName in SpecNames)
            ImportExplicitGrantedRules(specName, actionsByName, effectsById, effectsByName);

        foreach (var (abilityId, effects) in effectsById)
            byId[abilityId] = new ActionStateRule { Effects = MergeEffects(effects) };

        foreach (var (abilityName, effects) in effectsByName)
            byName[abilityName] = new ActionStateRule { Effects = MergeEffects(effects) };

        foreach (var (abilityName, effects) in effectsByName)
        {
            foreach (var effect in effects.Where(effect => effect.Delta > 0))
            {
                if (!grantorsByState.TryGetValue(effect.StateName, out var grantors))
                {
                    grantors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    grantorsByState[effect.StateName] = grantors;
                }

                grantors.Add(abilityName);
            }
        }
    }

    public ActionStateRule? Lookup(int abilityId, string abilityName)
    {
        if (abilityId > 0 && byId.TryGetValue((uint)abilityId, out var byIdRule))
            return byIdRule;

        return byName.GetValueOrDefault(abilityName);
    }

    public IReadOnlyCollection<string> GetGrantors(string stateName)
        => grantorsByState.TryGetValue(stateName, out var grantors)
            ? grantors
            : Array.Empty<string>();

    public bool HasGrantorInSet(string stateName, ISet<string> abilityNames)
        => grantorsByState.TryGetValue(stateName, out var grantors) &&
           grantors.Overlaps(abilityNames);

    private static void ImportExplicitGrantedRules(
        string specName,
        Dictionary<string, RecastDatabase.RecastInfo> actionsByName,
        Dictionary<uint, List<ActionStateEffect>> effectsById,
        Dictionary<string, List<ActionStateEffect>> effectsByName)
    {
        var rules = GrantedActionDatabase.GetRules(specName);
        if (rules == null)
            return;

        var resourcesByName = rules.Resources.ToDictionary(resource => resource.Name, StringComparer.OrdinalIgnoreCase);
        var grantorsByResource = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var consumersByResource = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (abilityName, effects) in rules.EffectByName)
        {
            foreach (var effect in effects)
            {
                AddExplicitStateEffect(actionsByName, effectsById, effectsByName, abilityName, new ActionStateEffect
                {
                    StateName = effect.ResourceName,
                    MinRequired = effect.MinRequired,
                    Delta = effect.Delta,
                    MaxValue = resourcesByName.TryGetValue(effect.ResourceName, out var resource)
                        ? resource.MaxValue
                        : 1,
                });

                if (effect.Delta > 0)
                {
                    if (!grantorsByResource.TryGetValue(effect.ResourceName, out var grantors))
                    {
                        grantors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        grantorsByResource[effect.ResourceName] = grantors;
                    }

                    grantors.Add(abilityName);
                }

                // Pure "clear the old grant" effects should not make the clearing action
                // depend on that granted state existing. This matters for AST draws:
                // Astral Draw and Umbral Draw clear the opposite hand, but neither draw
                // should require the other hand to be active before it can be used.
                if (effect.MinRequired > 0)
                {
                    if (!consumersByResource.TryGetValue(effect.ResourceName, out var consumers))
                    {
                        consumers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        consumersByResource[effect.ResourceName] = consumers;
                    }

                    consumers.Add(abilityName);
                }
            }
        }

        foreach (var rule in rules.RepeatableGrantedActionRules)
        {
            if (!rule.SkipCooldownWhenConsuming &&
                !rule.BypassGaugeSpendChecksWhenConsuming)
            {
                continue;
            }

            AddExplicitStateEffect(actionsByName, effectsById, effectsByName, rule.TriggerName, new ActionStateEffect
            {
                StateName = rule.ResourceName,
                Delta = rule.GrantCount,
                MaxValue = resourcesByName.TryGetValue(rule.ResourceName, out var grantResource)
                    ? grantResource.MaxValue
                    : rule.GrantCount,
            });

            foreach (var consumerName in rule.ConsumerNames)
            {
                AddExplicitStateEffect(actionsByName, effectsById, effectsByName, consumerName, new ActionStateEffect
                {
                    StateName = rule.ResourceName,
                    MinRequired = rule.ConsumeCount,
                    Delta = -rule.ConsumeCount,
                    MaxValue = resourcesByName.TryGetValue(rule.ResourceName, out var consumeResource)
                        ? consumeResource.MaxValue
                        : rule.GrantCount,
                });
            }
        }

        foreach (var (resourceName, grantors) in grantorsByResource)
        {
            if (!consumersByResource.TryGetValue(resourceName, out var consumers))
                continue;

            foreach (var consumerName in consumers)
            {
                var actionGrantState = BuildGrantedActionStateName(consumerName);
                foreach (var grantorName in grantors)
                {
                    AddExplicitStateEffect(actionsByName, effectsById, effectsByName, grantorName, new ActionStateEffect
                    {
                        StateName = actionGrantState,
                        Delta = 1,
                        MaxValue = 1,
                    });
                }

                AddExplicitStateEffect(actionsByName, effectsById, effectsByName, consumerName, new ActionStateEffect
                {
                    StateName = actionGrantState,
                    MinRequired = 1,
                    Delta = -1,
                    MaxValue = 1,
                });
            }
        }
    }

    private static void ImportSheetStatuses(
        RecastDatabase.RecastInfo action,
        Dictionary<uint, List<ActionStateEffect>> effectsById,
        Dictionary<string, List<ActionStateEffect>> effectsByName)
    {
        if (!string.IsNullOrWhiteSpace(action.ProcStatusName))
        {
            AddEffect(effectsById, effectsByName, action.AbilityId, action.Name, new ActionStateEffect
            {
                StateName = NormalizeStateName(action.ProcStatusName),
                MinRequired = 1,
                Delta = ShouldConsumeRequiredState(action.ProcStatusName) ? -1 : 0,
                MaxValue = Math.Max(1, action.ProcStatusMaxStacks),
            });
        }

        if (!string.IsNullOrWhiteSpace(action.SelfStatusName))
        {
            AddEffect(effectsById, effectsByName, action.AbilityId, action.Name, new ActionStateEffect
            {
                StateName = NormalizeStateName(action.SelfStatusName),
                Delta = 1,
                MaxValue = Math.Max(1, action.SelfStatusMaxStacks),
            });
        }
    }

    private static void AddExplicitStateEffect(
        Dictionary<string, RecastDatabase.RecastInfo> actionsByName,
        Dictionary<uint, List<ActionStateEffect>> effectsById,
        Dictionary<string, List<ActionStateEffect>> effectsByName,
        string abilityName,
        ActionStateEffect effect)
    {
        if (actionsByName.TryGetValue(abilityName, out var action))
            AddEffect(effectsById, effectsByName, action.AbilityId, action.Name, effect);
        else
            AddEffect(effectsByName, abilityName, effect);
    }

    private static IReadOnlyList<ActionStateEffect> MergeEffects(IEnumerable<ActionStateEffect> effects)
    {
        return effects
            .GroupBy(effect => effect.StateName, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ActionStateEffect
            {
                StateName = group.Key,
                MinRequired = group.Max(effect => effect.MinRequired),
                Delta = group.Sum(effect => effect.Delta),
                MaxValue = group.Max(effect => effect.MaxValue),
            })
            .ToList();
    }

    private static void AddEffect(
        Dictionary<uint, List<ActionStateEffect>> effectsById,
        Dictionary<string, List<ActionStateEffect>> effectsByName,
        uint abilityId,
        string abilityName,
        ActionStateEffect effect)
    {
        if (!effectsById.TryGetValue(abilityId, out var byIdList))
        {
            byIdList = [];
            effectsById[abilityId] = byIdList;
        }

        byIdList.Add(effect);
        AddEffect(effectsByName, abilityName, effect);
    }

    private static void AddEffect(
        Dictionary<string, List<ActionStateEffect>> effectsByName,
        string abilityName,
        ActionStateEffect effect)
    {
        if (!effectsByName.TryGetValue(abilityName, out var byNameList))
        {
            byNameList = [];
            effectsByName[abilityName] = byNameList;
        }

        byNameList.Add(effect);
    }

    private static string NormalizeStateName(string raw)
        => raw
            .Trim()
            .TrimEnd('.')
            .TrimEnd(':')
            .Replace("  ", " ", StringComparison.Ordinal);

    private static bool ShouldConsumeRequiredState(string stateName)
    {
        var normalized = stateName.Trim().ToLowerInvariant();
        return ConsumableStateTokens.Any(token => normalized.Contains(token, StringComparison.Ordinal));
    }

    private static string BuildGrantedActionStateName(string actionName)
        => $"Action Grant::{actionName}";
}
