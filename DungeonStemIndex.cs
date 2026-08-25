using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace HDM;

/// <summary>
/// Loads Data/mob-dungeon-stems.csv — a small CURATED <c>Stem → TerritoryType</c> table that
/// resolves a mob to its home duty by the DUNGEON WORD in its NAME. Schema:
/// <c>Stem,TerritoryTypeId,Label</c> (Label is free text and may contain commas; <c>#</c>-lead
/// lines are comments).
///
/// Why it exists (the crack): FFXIV dungeon/raid TRASH is director-spawned at runtime — it is
/// placed in NO flat client sheet (zero Level Type-9 rows, absent from the BossMod instanced
/// roster, which only carries bosses). Every automated location tier therefore structurally
/// misses it, and crowdsource telemetry SCATTERS it: the same model is reused by overworld mobs,
/// so a "Babil slasher" gets reported wherever its shared model was last seen, not in the Tower
/// of Babil. The one authored signal that survives is the NAME: Square names dungeon mobs after
/// the dungeon ("<b>Babil</b> slasher", "<b>Aloalo</b> ogrebon"). A single curated stem rule
/// —"any mob whose name contains the whole word <i>Babil</i> lives in territory 969"— places the
/// entire roster at once, which is why this scales where per-base manual tags don't (one
/// "Aloalo,1176" line replaces 63 hand-tags).
///
/// Authority: a stem asserts a hand-verified fact ("this word only occurs on this duty's mobs"),
/// so it sits at Tier N — directly BELOW the Tier M per-base curated tags and ABOVE crowdsource,
/// deliberately overriding the scattered telemetry that would otherwise misfile the mob. Add a
/// stem ONLY after confirming (over the shipped catalog) that the word occurs on no unrelated mob;
/// a generic word that a duty happens to reuse ("tempered", "imperial") is NOT a valid stem —
/// those mobs carry no dungeon word and are the irreducible exception this tier cannot reach.
///
/// It also carries the human-readable <see cref="TryGetNote"/> string so the target inspector can
/// explain WHY a mob resolves to a duty it was never observed in ("Tower of Babil — dungeon trash,
/// name-stem rule") — the provenance a bare TerritoryType id can't convey.
/// </summary>
public sealed class DungeonStemIndex
{
    // Lowercased stem + its territory + label, in file order (first match wins on the rare
    // overlap). Stored flat rather than as a dict because matching is whole-word CONTAINMENT,
    // not equality — a mob name isn't a key, the stem is a substring of it.
    private readonly (string Stem, uint Territory, string Label)[] _stems;

    /// <summary>How many curated dungeon-name stems are loaded.</summary>
    public int Count => _stems.Length;

    /// <summary>
    /// Resolve a mob name to a home TerritoryType by matching a curated dungeon stem as a WHOLE
    /// WORD (bounded by non-letters, case-insensitive) anywhere in the name. Whole-word is the
    /// safety gate: it matches "Babil slasher" and "Babil Sky Armor" but never a name that merely
    /// contains the letters mid-word. False when no stem matches.
    /// </summary>
    public bool TryMatch(string mobName, out uint territoryId)
    {
        if (TryMatchInternal(mobName, out territoryId, out _)) return true;
        territoryId = 0;
        return false;
    }

    /// <summary>The curated provenance label for the stem that matches this name (empty when none).</summary>
    public bool TryGetNote(string mobName, out string note)
    {
        if (TryMatchInternal(mobName, out _, out var label) && label.Length > 0) { note = label; return true; }
        note = "";
        return false;
    }

    private bool TryMatchInternal(string mobName, out uint territoryId, out string label)
    {
        if (!string.IsNullOrEmpty(mobName))
        {
            var name = mobName.ToLowerInvariant();
            foreach (var (stem, terr, lbl) in _stems)
            {
                if (ContainsWord(name, stem)) { territoryId = terr; label = lbl; return true; }
            }
        }
        territoryId = 0;
        label = "";
        return false;
    }

    /// <summary>Whole-word containment: <paramref name="stem"/> occurs in <paramref name="haystack"/>
    /// (both already lowercased) bounded on each side by a non-letter (or string edge), so "aloalo"
    /// matches "aloalo ogrebon" but "ala" never matches "alabaster".</summary>
    private static bool ContainsWord(string haystack, string stem)
    {
        if (stem.Length == 0) return false;
        var from = 0;
        while (true)
        {
            var i = haystack.IndexOf(stem, from, StringComparison.Ordinal);
            if (i < 0) return false;
            var beforeOk = i == 0 || !char.IsLetter(haystack[i - 1]);
            var after = i + stem.Length;
            var afterOk = after >= haystack.Length || !char.IsLetter(haystack[after]);
            if (beforeOk && afterOk) return true;
            from = i + 1;
        }
    }

    public DungeonStemIndex(IDalamudPluginInterface pi, IPluginLog log)
    {
        var stems = new List<(string, uint, string)>();
        var path = Path.Combine(pi.AssemblyLocation.DirectoryName!, "Data", "mob-dungeon-stems.csv");
        try
        {
            using var r = new StreamReader(path, Encoding.UTF8);
            r.ReadLine(); // header
            string? line;
            while ((line = r.ReadLine()) != null)
            {
                if (line.Length == 0 || line[0] == '#') continue; // blank or comment
                // Split on the first two commas only, so a Label field may itself contain commas.
                var f = line.Split(',', 3);
                if (f.Length < 2) continue;
                var stem = f[0].Trim().ToLowerInvariant();
                if (stem.Length == 0) continue;
                if (!uint.TryParse(f[1].Trim(), out var terr)) continue;
                stems.Add((stem, terr, f.Length > 2 ? f[2].Trim() : ""));
            }
            log.Information($"DungeonStemIndex: {stems.Count} dungeon-name stems loaded from {path}");
        }
        catch (Exception e)
        {
            log.Error(e, $"DungeonStemIndex: failed to load {path}");
        }

        _stems = stems.ToArray();
    }
}
