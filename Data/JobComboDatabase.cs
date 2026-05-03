using System;
using System.Collections.Generic;
using System.Linq;

namespace ATKTip.Data;

public static class JobComboDatabase
{
    public sealed class JobComboHints
    {
        public HashSet<string> ComboStarters { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyList<IReadOnlyList<string>> ComboLines { get; init; } = [];
    }

    private static readonly Dictionary<string, JobComboHints> BySpec =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Paladin"] = J(
                ["Fast Blade", "Total Eclipse"],
                L("Fast Blade", "Riot Blade", "Royal Authority"),
                L("Confiteor", "Blade of Faith", "Blade of Truth", "Blade of Valor"),
                L("Total Eclipse", "Prominence")),

            ["Warrior"] = J(
                ["Heavy Swing", "Overpower"],
                L("Heavy Swing", "Maim", "Storm's Path"),
                L("Heavy Swing", "Maim", "Storm's Eye"),
                L("Overpower", "Mythril Tempest")),

            ["Dark Knight"] = J(
                ["Hard Slash", "Unleash"],
                L("Hard Slash", "Syphon Strike", "Souleater"),
                L("Scarlet Delirium", "Comeuppance", "Torcleaver"),
                L("Unleash", "Stalwart Soul")),

            ["Gunbreaker"] = J(
                ["Keen Edge", "Demon Slice", "Gnashing Fang"],
                L("Keen Edge", "Brutal Shell", "Solid Barrel"),
                L("Demon Slice", "Demon Slaughter"),
                L("Gnashing Fang", "Jugular Rip", "Savage Claw", "Abdomen Tear", "Wicked Talon", "Eye Gouge"),
                L("Reign of Beasts", "Noble Blood", "Lion Heart")),

            ["Monk"] = J(
                ["Bootshine", "Dragon Kick", "Shadow of the Destroyer"],
                L("Bootshine", "True Strike", "Snap Punch"),
                L("Dragon Kick", "Twin Snakes", "Demolish"),
                L("Shadow of the Destroyer", "Four-point Fury", "Rockbreaker")),

            ["Dragoon"] = J(
                ["True Thrust", "Raiden Thrust", "Doom Spike"],
                L("True Thrust", "Lance Barrage", "Heavens' Thrust", "Fang and Claw", "Drakesbane"),
                L("Raiden Thrust", "Lance Barrage", "Heavens' Thrust", "Fang and Claw", "Drakesbane"),
                L("True Thrust", "Spiral Blow", "Chaotic Spring", "Wheeling Thrust", "Drakesbane"),
                L("Raiden Thrust", "Spiral Blow", "Chaotic Spring", "Wheeling Thrust", "Drakesbane"),
                L("Doom Spike", "Sonic Thrust", "Coerthan Torment")),

            ["Ninja"] = J(
                ["Spinning Edge", "Death Blossom"],
                L("Spinning Edge", "Gust Slash", "Aeolian Edge"),
                L("Spinning Edge", "Gust Slash", "Armor Crush"),
                L("Death Blossom", "Hakke Mujinsatsu")),

            ["Reaper"] = J(
                ["Slice", "Spinning Scythe"],
                L("Slice", "Waxing Slice", "Infernal Slice"),
                L("Spinning Scythe", "Nightmare Scythe")),

            ["Samurai"] = J(
                ["Hakaze", "Fuko"],
                L("Hakaze", "Jinpu", "Gekko"),
                L("Hakaze", "Shifu", "Kasha"),
                L("Hakaze", "Yukikaze"),
                L("Fuko", "Mangetsu"),
                L("Fuko", "Oka")),

            ["Viper"] = J(
                ["Steel Fangs", "Reaving Fangs", "Steel Maw", "Reaving Maw", "Vicewinder", "Vicepit"],
                L("Steel Fangs", "Hunter's Sting", "Flanksting Strike", "Flanksbane Fang"),
                L("Steel Fangs", "Swiftskin's Sting", "Hindsting Strike", "Hindsbane Fang"),
                L("Reaving Fangs", "Swiftskin's Sting", "Hindsting Strike", "Hindsbane Fang"),
                L("Reaving Fangs", "Hunter's Sting", "Flanksting Strike", "Flanksbane Fang"),
                L("Steel Maw", "Hunter's Bite", "Jagged Maw"),
                L("Steel Maw", "Swiftskin's Bite", "Bloodied Maw"),
                L("Reaving Maw", "Swiftskin's Bite", "Bloodied Maw"),
                L("Reaving Maw", "Hunter's Bite", "Jagged Maw"),
                L("Vicewinder", "Hunter's Coil", "Swiftskin's Coil"),
                L("Vicewinder", "Swiftskin's Coil", "Hunter's Coil"),
                L("Vicepit", "Hunter's Den", "Swiftskin's Den"),
                L("Vicepit", "Swiftskin's Den", "Hunter's Den")),

            ["Dancer"] = J(
                ["Cascade", "Windmill"],
                L("Cascade", "Fountain"),
                L("Windmill", "Bladeshower")),

            ["Machinist"] = J(
                ["Heated Split Shot"],
                L("Heated Split Shot", "Heated Slug Shot", "Heated Clean Shot")),

            ["Red Mage"] = J(
                ["Enchanted Riposte", "Enchanted Moulinet"],
                L("Enchanted Riposte", "Enchanted Zwerchhau", "Enchanted Redoublement"),
                L("Enchanted Redoublement", "Verholy", "Scorch", "Resolution"),
                L("Enchanted Redoublement", "Verflare", "Scorch", "Resolution"),
                L("Enchanted Moulinet", "Enchanted Moulinet Deux", "Enchanted Moulinet Trois"))
        };

    private static readonly Dictionary<string, HashSet<string>> FollowersByName =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, HashSet<string>> PredecessorsByName =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> StartersByName =
        new(StringComparer.OrdinalIgnoreCase);

    static JobComboDatabase()
    {
        foreach (var hints in BySpec.Values)
        {
            foreach (var starter in hints.ComboStarters)
                StartersByName.Add(starter);

            foreach (var line in hints.ComboLines)
            {
                if (line.Count > 0)
                    StartersByName.Add(line[0]);

                for (var i = 0; i < line.Count - 1; i++)
                {
                    var current = line[i];
                    var next = line[i + 1];

                    if (!FollowersByName.TryGetValue(current, out var followers))
                    {
                        followers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        FollowersByName[current] = followers;
                    }

                    followers.Add(next);

                    if (!PredecessorsByName.TryGetValue(next, out var predecessors))
                    {
                        predecessors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        PredecessorsByName[next] = predecessors;
                    }

                    predecessors.Add(current);
                }
            }
        }
    }

    public static JobComboHints? GetHints(string specName)
        => BySpec.GetValueOrDefault(specName);

    public static bool IsComboStarter(string abilityName)
        => StartersByName.Contains(abilityName);

    public static HashSet<string> GetFollowers(string abilityName)
        => FollowersByName.TryGetValue(abilityName, out var followers)
            ? new HashSet<string>(followers, StringComparer.OrdinalIgnoreCase)
            : [];

    public static HashSet<string> GetPredecessors(string abilityName)
        => PredecessorsByName.TryGetValue(abilityName, out var predecessors)
            ? new HashSet<string>(predecessors, StringComparer.OrdinalIgnoreCase)
            : [];

    private static JobComboHints J(
        string[] starters,
        params IReadOnlyList<string>[] lines)
        => new()
        {
            ComboStarters = new HashSet<string>(starters, StringComparer.OrdinalIgnoreCase),
            ComboLines = lines,
        };

    private static IReadOnlyList<string> L(params string[] values)
        => values;
}
