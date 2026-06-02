using System;
using System.Collections.Generic;
using System.Linq;

namespace ATKTip.Data;

public static class GrantedActionDatabase
{
    public sealed class GrantedActionResource
    {
        public string Name { get; init; } = string.Empty;
        public int MaxValue { get; init; }
        public int InitialValue { get; init; }
    }

    public sealed class GrantedActionEffect
    {
        public string ResourceName { get; init; } = string.Empty;
        public int MinRequired { get; init; }
        public int Delta { get; init; }
    }

    public sealed class CardDrawRules
    {
        public string AstralDrawName { get; init; } = "Astral Draw";
        public string UmbralDrawName { get; init; } = "Umbral Draw";
        public HashSet<string> AstralCards { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> UmbralCards { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class InstantCastRule
    {
        public string ResourceName { get; init; } = string.Empty;
        public bool AppliesToAnyCastTimeSpell { get; init; }
        public HashSet<string> AbilityNames { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public int MinRequired { get; init; } = 1;
        public int Consume { get; init; } = 1;
    }

    public sealed class HardcastGrantRule
    {
        public string ResourceName { get; init; } = string.Empty;
        public bool AppliesToAnyCastTimeSpell { get; init; }
        public HashSet<string> AbilityNames { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public int Delta { get; init; } = 1;
    }

    public sealed class RepeatableGrantedActionRule
    {
        public string TriggerName { get; init; } = string.Empty;
        public string ResourceName { get; init; } = string.Empty;
        public int GrantCount { get; init; } = 1;
        public int ConsumeCount { get; init; } = 1;
        public double? WindowDurationSec { get; init; }
        public bool TriggerConsumesWhenResourcePresent { get; init; }
        public bool SkipCooldownWhenConsuming { get; init; }
        public bool BypassGaugeSpendChecksWhenConsuming { get; init; }
        public HashSet<string> ConsumerNames { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyList<GaugeSimulator.GaugeEffect> ConsumeBonusEffects { get; init; } = [];
    }

    public sealed class JobGrantedActionRules
    {
        public IReadOnlyList<GrantedActionResource> Resources { get; init; } = [];
        public Dictionary<string, IReadOnlyList<GrantedActionEffect>> EffectByName { get; init; }
            = new(StringComparer.OrdinalIgnoreCase);
        public CardDrawRules? CardDraw { get; init; }
        public IReadOnlyList<InstantCastRule> InstantCastRules { get; init; } = [];
        public IReadOnlyList<HardcastGrantRule> HardcastGrantRules { get; init; } = [];
        public IReadOnlyList<RepeatableGrantedActionRule> RepeatableGrantedActionRules { get; init; } = [];
    }

    public static JobGrantedActionRules? GetRules(string specName)
    {
        var mixed = GaugeSimulator.GetMixedRules(specName);
        if (mixed == null)
            return null;

        var resources = mixed.Resources
            .Where(resource => !GaugeSimulator.IsTrueGaugeResource(resource.Name))
            .Select(resource => new GrantedActionResource
            {
                Name = resource.Name,
                MaxValue = resource.MaxValue,
                InitialValue = resource.InitialValue,
            })
            .ToList();
        var allowed = resources
            .Select(resource => resource.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var effects = new Dictionary<string, IReadOnlyList<GrantedActionEffect>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (abilityName, abilityEffects) in mixed.EffectByName)
        {
            var filtered = abilityEffects
                .Where(effect => allowed.Contains(effect.GaugeName))
                .Select(effect => new GrantedActionEffect
                {
                    ResourceName = effect.GaugeName,
                    MinRequired = effect.MinRequired,
                    Delta = effect.Delta,
                })
                .ToList();
            if (filtered.Count > 0)
                effects[abilityName] = filtered;
        }

        return new JobGrantedActionRules
        {
            Resources = resources,
            EffectByName = effects,
            CardDraw = mixed.CardDraw == null
                ? null
                : new CardDrawRules
                {
                    AstralDrawName = mixed.CardDraw.AstralDrawName,
                    UmbralDrawName = mixed.CardDraw.UmbralDrawName,
                    AstralCards = new HashSet<string>(mixed.CardDraw.AstralCards, StringComparer.OrdinalIgnoreCase),
                    UmbralCards = new HashSet<string>(mixed.CardDraw.UmbralCards, StringComparer.OrdinalIgnoreCase),
                },
            InstantCastRules = mixed.InstantCastRules
                .Where(rule => allowed.Contains(rule.ResourceName))
                .Select(rule => new InstantCastRule
                {
                    ResourceName = rule.ResourceName,
                    AppliesToAnyCastTimeSpell = rule.AppliesToAnyCastTimeSpell,
                    AbilityNames = new HashSet<string>(rule.AbilityNames, StringComparer.OrdinalIgnoreCase),
                    MinRequired = rule.MinRequired,
                    Consume = rule.Consume,
                })
                .ToList(),
            HardcastGrantRules = mixed.HardcastGrantRules
                .Where(rule => allowed.Contains(rule.ResourceName))
                .Select(rule => new HardcastGrantRule
                {
                    ResourceName = rule.ResourceName,
                    AppliesToAnyCastTimeSpell = rule.AppliesToAnyCastTimeSpell,
                    AbilityNames = new HashSet<string>(rule.AbilityNames, StringComparer.OrdinalIgnoreCase),
                    Delta = rule.Delta,
                })
                .ToList(),
            RepeatableGrantedActionRules = mixed.RepeatableGrantedActionRules
                .Where(rule => allowed.Contains(rule.ResourceName))
                .Select(rule => new RepeatableGrantedActionRule
                {
                    TriggerName = rule.TriggerName,
                    ResourceName = rule.ResourceName,
                    GrantCount = rule.GrantCount,
                    ConsumeCount = rule.ConsumeCount,
                    WindowDurationSec = rule.WindowDurationSec,
                    TriggerConsumesWhenResourcePresent = rule.TriggerConsumesWhenResourcePresent,
                    SkipCooldownWhenConsuming = rule.SkipCooldownWhenConsuming,
                    BypassGaugeSpendChecksWhenConsuming = rule.BypassGaugeSpendChecksWhenConsuming,
                    ConsumerNames = new HashSet<string>(rule.ConsumerNames, StringComparer.OrdinalIgnoreCase),
                    ConsumeBonusEffects = rule.ConsumeBonusEffects
                        .Select(effect => new GaugeSimulator.GaugeEffect
                        {
                            GaugeName = effect.GaugeName,
                            MinRequired = effect.MinRequired,
                            MaxAllowedBeforeUse = effect.MaxAllowedBeforeUse,
                            SetValue = effect.SetValue,
                            Delta = effect.Delta,
                        })
                        .ToList(),
                })
                .ToList(),
        };
    }
}
