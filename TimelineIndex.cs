using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace HDM;

/// <summary>One catalogued action timeline: a game ActionTimeline row with a
/// friendly, heuristically-derived name.</summary>
public sealed record TimelineRow(uint Id, string Cat, string Skel, string Key, string Name);

/// <summary>
/// Loads Data/timeline-index.csv (columns <c>Id,Cat,Skel,Key</c>), the offline
/// Fable deliverable that turns opaque timeline ids into named buttons.
///
///  - "common" rows (empty Skel) are base-game timelines most skeletons share:
///    idle, battle stance, walk/run, jumps, reactions.
///  - "monster" rows carry a skeleton code (mNNNN) and a <c>mon_sp/mNNNN/mon_spNNN</c>
///    key — the model's own specials (attacks, roars, emotes).
///
/// Names are best-effort: derived from the Key path (<see cref="Prettify"/>), with two offline
/// skill-name overlays layered on top where a safe 1:1 mapping exists — special-names.csv for
/// per-skeleton monster specials, and common-skill-names.csv for shared class cast poses
/// (battle/magic_pt11 -> "Hammer Motif"). The raw key travels with the row so the UI can show
/// ground truth on hover, and a richer per-skeleton catalog remains a queued Fable deliverable.
///
/// "Legality" is inherently per-skeleton and only heuristic here: a guised actor
/// can play any timeline its CURRENT skeleton actually defines. We surface the
/// selected mob's own specials plus the shared Common set; playing one a skeleton
/// lacks is a harmless no-op.
/// </summary>
public sealed class TimelineIndex
{
    /// <summary>Base-game timelines shared across skeletons, sorted by name.</summary>
    public IReadOnlyList<TimelineRow> Common { get; }

    /// <summary>Playable emotes (Emote sheet → each emote's standing <c>ActionTimeline[0]</c>), sorted by
    /// name. Surfaced for HUMAN (cNNNN) guises, whose bodies actually play /point, /wave, /dance &amp;c;
    /// a monster skeleton would no-op them (harmless, per the same "playing one a skeleton lacks is a
    /// no-op" rule as Common), so the UI shows this group only for a human guise. Each row travels the
    /// SAME <c>DrawTimelineButtons → TriggerTimeline → AnimationService</c> funnel as every other timeline
    /// (Rule 1: one mechanism), so it loops/plays-once and is searchable with no bespoke path.</summary>
    public IReadOnlyList<TimelineRow> Emotes { get; }

    // mNNNN -> that skeleton's specials, in id order.
    private readonly Dictionary<string, List<TimelineRow>> _bySkel;
    private static readonly IReadOnlyList<TimelineRow> Empty = Array.Empty<TimelineRow>();

    // mNNNN/dNNNN -> the internal HAVOK animation names its resident .pap bundles define
    // (Data/skel-anim-caps.csv, an offline extraction of every monster/demihuman resident
    // .pap Name table). This is the PHYSICAL ORACLE of "what this skeleton can actually
    // play" — the fact the old chip-splitter comments said "can't exist offline". It can,
    // and this is it. Absent for human (cNNNN) skeletons: those keep the full Common list,
    // because a player body genuinely DOES walk/run/emote/cast.
    private readonly Dictionary<string, HashSet<string>> _capsBySkel;

    // Memo of ValidCommonFor results by skeleton. The trimmed-Common computation is a GroupBy/OrderBy over
    // the ~440-row Common list — cheap once, but the Favourites tab calls it per-favourite per-FRAME, so
    // cache it. The caps table and Common list are immutable after load, so the memo never goes stale;
    // access is single-threaded (UI draw / framework thread), so a plain Dictionary is safe.
    private readonly Dictionary<string, IReadOnlyList<TimelineRow>> _validCommonMemo = new(64);

    // Common timeline Key -> its global ActionTimeline id. A Common timeline shares ONE id across every
    // skeleton (the skeleton's resident .pap supplies the actual HAVOK clip the id references), so this is
    // the key->id half of <see cref="ResolvePlayable"/>: the caps set says whether a skeleton CAN play a
    // key, this says which id to fire. Built from the de-duped Common list after load; empty if the CSV
    // failed to load (ResolvePlayable then returns 0 for everything — graceful, the Combos just don't show).
    private readonly Dictionary<string, uint> _commonKeyToId = new(512, StringComparer.Ordinal);

    // ActionTimeline id -> real ability name for a monster special (Data/special-names.csv,
    // joined offline from the Action sheet: Action.AnimationEnd/ActionTimelineHit -> the
    // mon_sp timeline it plays -> Action.Name). Turns "Special 003" into "Firestorm".
    private readonly Dictionary<uint, string> _specialNames;

    // ActionTimeline id -> recognisable player-CLASS-skill name for a shared COMMON cast pose
    // (Data/common-skill-names.csv, joined offline: Action.AnimationStart -> ActionCastTimeline ->
    // that cast-pose ActionTimeline, restricted to job-affiliated battle/ poses that exactly ONE
    // skill maps to). Turns the lossy key heuristic "Battle Magic Pt11" into "Hammer Motif" and its
    // _loop sibling into "Hammer Motif (loop)". Deliberately narrow: shared poses (magic_thm_start,
    // 140 spells) and generic per-monster slots (battle/mon_sp*) are excluded by the generator, so a
    // hit here is always a safe 1:1 relabel. Applied to Common rows only (see the loader loop).
    private readonly Dictionary<uint, string> _commonSkillNames;

    /// <summary>
    /// Codebook: an ActionTimeline <c>Key</c> -> the internal HAVOK animation name a monster/demihuman
    /// resident .pap must define for that timeline to actually animate. This mapping is an ENGINE NAMING
    /// CONVENTION (battle/ -> cbbm_, normal/ -> cbnm_; idle->id0, dead->ded, auto_attack1->atk1,
    /// turn_loop_l->trn_l_lp), stored in no sheet — it's the bridge that separates a working button from a
    /// dummy. Two lanes live here, both validated against a skeleton's caps set (<see cref="ValidCommonFor"/>):
    ///  - BASE-LANE (idle/attack/dead/turn/cast): rock-solid correspondences.
    ///  - GENERIC RESIDENT SPECIALS (battle/mon_sp_a..l_start/loop -> cbbm_sp_a..l_1 / _2lp): the
    ///    slot-lettered specials baked into a model's resident .pap and fired through a GENERIC, skeleton-
    ///    agnostic ActionTimeline. These are the ONLY named-UI path to a boss's own attacks when it has
    ///    resident specials but NO standalone mon_sp/mNNNN rows — e.g. Galatea Magna (m0333) binds
    ///    cbbm_sp_a/b/c_2lp yet has zero rows in timeline-index.csv, so <see cref="ForSkeleton"/> finds
    ///    nothing and only this lane can surface them. The caps gate keeps them honest per skeleton.
    /// STANDALONE per-skeleton specials (mon_sp/mNNNN/mon_spNNN) are deliberately NOT here: they are
    /// validated by per-skeleton FILE existence (<see cref="ForSkeleton"/>) and carry real skill names
    /// (special-names.csv), so routing them through this table would only duplicate them under worse names.
    /// A wrong entry can at worst over-/under-show ONE button, always recoverable via "Show all timelines".
    /// </summary>
    private static readonly Dictionary<string, string> Codebook = new(StringComparer.Ordinal)
    {
        ["battle/idle"] = "cbbm_id0",
        ["battle/auto_attack1"] = "cbbm_atk1",
        ["battle/auto_attack1_mon_a"] = "cbbm_atk1",
        ["battle/auto_attack1_mon_b"] = "cbbm_atk1",
        ["battle/auto_attack_shot1"] = "cbbm_sht1",
        ["battle/auto_attack_shot1_mon"] = "cbbm_sht1",
        ["battle/dead"] = "cbbm_ded",
        ["battle/dead_pose"] = "cbbm_dedpose",
        ["battle/turn_loop_l"] = "cbbm_trn_l_lp",
        ["battle/turn_loop_r"] = "cbbm_trn_r_lp",
        ["battle/partsbreak"] = "cbbm_partsbreak",
        ["battle/magic_thm_start"] = "cbbm_mgc_thm_1",
        ["battle/magic_thm_loop"] = "cbbm_mgc_thm_2lp",
        ["normal/idle"] = "cbnm_id0",
        ["normal/dead_pose"] = "cbnm_dedpose",
        ["normal/turn_loop_l"] = "cbnm_trn_l_lp",
        ["normal/turn_loop_r"] = "cbnm_trn_r_lp",

        // Generic resident-special triggers: the slot-lettered specials in a model's resident .pap, each
        // surfaced ONLY when the skeleton's caps actually bind that slot (so m0333 shows a/b/c, not d..l).
        // _start = intro (cbbm_sp_X_1), _loop = the held special pose (cbbm_sp_X_2lp).
        ["battle/mon_sp_a_start"] = "cbbm_sp_a_1", ["battle/mon_sp_a_loop"] = "cbbm_sp_a_2lp",
        ["battle/mon_sp_b_start"] = "cbbm_sp_b_1", ["battle/mon_sp_b_loop"] = "cbbm_sp_b_2lp",
        ["battle/mon_sp_c_start"] = "cbbm_sp_c_1", ["battle/mon_sp_c_loop"] = "cbbm_sp_c_2lp",
        ["battle/mon_sp_d_start"] = "cbbm_sp_d_1", ["battle/mon_sp_d_loop"] = "cbbm_sp_d_2lp",
        ["battle/mon_sp_e_start"] = "cbbm_sp_e_1", ["battle/mon_sp_e_loop"] = "cbbm_sp_e_2lp",
        ["battle/mon_sp_f_start"] = "cbbm_sp_f_1", ["battle/mon_sp_f_loop"] = "cbbm_sp_f_2lp",
        ["battle/mon_sp_g_start"] = "cbbm_sp_g_1", ["battle/mon_sp_g_loop"] = "cbbm_sp_g_2lp",
        ["battle/mon_sp_h_start"] = "cbbm_sp_h_1", ["battle/mon_sp_h_loop"] = "cbbm_sp_h_2lp",
        ["battle/mon_sp_i_start"] = "cbbm_sp_i_1", ["battle/mon_sp_i_loop"] = "cbbm_sp_i_2lp",
        ["battle/mon_sp_j_start"] = "cbbm_sp_j_1", ["battle/mon_sp_j_loop"] = "cbbm_sp_j_2lp",
        ["battle/mon_sp_k_start"] = "cbbm_sp_k_1", ["battle/mon_sp_k_loop"] = "cbbm_sp_k_2lp",
        ["battle/mon_sp_l_start"] = "cbbm_sp_l_1", ["battle/mon_sp_l_loop"] = "cbbm_sp_l_2lp",
    };

    public TimelineIndex(IDalamudPluginInterface pi, IDataManager data, IPluginLog log)
    {
        var common = new List<TimelineRow>(512);
        _bySkel = new Dictionary<string, List<TimelineRow>>(256);
        _capsBySkel = new Dictionary<string, HashSet<string>>(2048, StringComparer.Ordinal);
        _specialNames = new Dictionary<uint, string>(8192);
        _commonSkillNames = new Dictionary<uint, string>(64);

        // Emotes come LIVE from the game's Emote sheet (not the offline CSVs) — small, stable, and it lets
        // the catalog track the client's own patch without a regen. Independent of the CSV load below and
        // self-guarded, so a sheet miss just yields an empty Emotes group, never a dead plugin.
        Emotes = LoadEmotes(data, log);

        var dataDir = Path.Combine(pi.AssemblyLocation.DirectoryName!, "Data");
        // Side catalogs first so specials/common rows can be relabelled with real skill names as rows load.
        LoadSpecialNames(Path.Combine(dataDir, "special-names.csv"), log);
        LoadCaps(Path.Combine(dataDir, "skel-anim-caps.csv"), log);
        LoadCommonSkillNames(Path.Combine(dataDir, "common-skill-names.csv"), log);

        var path = Path.Combine(dataDir, "timeline-index.csv");
        try
        {
            using var r = new StreamReader(path, Encoding.UTF8);
            r.ReadLine(); // header
            string? line;
            while ((line = r.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                var f = line.Split(',');
                if (f.Length < 4) continue;
                if (!uint.TryParse(f[0], out var id)) continue;

                var cat = f[1];
                var skel = f[2];
                var key = f[3];

                if (skel.Length == 0)
                {
                    // Prefer a recognisable class-skill name (Hammer Motif) over the key heuristic
                    // (Battle Magic Pt11); the offline join only ever names safe 1:1 combat poses.
                    var cname = _commonSkillNames.TryGetValue(id, out var sk) && sk.Length > 0
                        ? sk : Prettify(key);
                    common.Add(new TimelineRow(id, cat, skel, key, cname));
                }
                else
                {
                    // _hit sub-timelines are damage-reaction helpers, never a standalone button,
                    // and would only dupe their parent's skill name — drop them here.
                    if (key.Contains("_hit", StringComparison.Ordinal)) continue;
                    // Prefer the real ability name (special-names.csv); else the "Special NNN" heuristic.
                    var name = _specialNames.TryGetValue(id, out var skill) && skill.Length > 0
                        ? skill : Prettify(key);
                    if (!_bySkel.TryGetValue(skel, out var list))
                        _bySkel[skel] = list = new List<TimelineRow>(16);
                    list.Add(new TimelineRow(id, cat, skel, key, name));
                }
            }

            // De-dupe common by friendly name (many ids collapse to "Idle" etc.),
            // keeping the lowest id, then sort for a stable, browsable list.
            Common = common
                .GroupBy(t => t.Name)
                .Select(g => g.OrderBy(t => t.Id).First())
                .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // key -> global id for the compound-gesture resolver (ResolvePlayable). Built from the de-duped
            // Common list; TryAdd keeps the first id if two keys ever collapse. Empty on a CSV failure below.
            foreach (var row in Common) _commonKeyToId.TryAdd(row.Key, row.Id);

            log.Information($"TimelineIndex: {Common.Count} common (of {common.Count}), " +
                            $"{_bySkel.Count} skeletons from {path}");
        }
        catch (Exception e)
        {
            log.Error(e, $"TimelineIndex: failed to load {path}");
            Common = Empty;
        }
    }

    /// <summary>
    /// Build the emote catalog from the live Emote sheet. Each emote's STANDING animation is its
    /// <c>ActionTimeline[0]</c> (validated against Brio's ActionTimelineSelector and ARealmRepopulated's
    /// NPC emote path, which fires exactly this id through <c>TimelineSequencer.PlayTimeline</c> — the same
    /// call HDM's <see cref="AnimationService.PlayOnce"/> makes). We take index 0 only: the sitting/chair
    /// variants ([1..4]) are stateful poses that need the EmoteController triplet, not a one-shot blend, so
    /// they'd only play their intro here — out of scope, and skipping a 0-timeline row drops the pure
    /// facial-expression emotes cleanly. Rows are de-duped by timeline id (two emotes that resolve to the
    /// same clip would be the same button) keeping the lowest emote row, then sorted by name. The <c>Key</c>
    /// carries the "/point" text command when present so the Animations-tab search matches either the label
    /// or the slash-command. Self-guarded: a sheet failure logs and yields an empty list.
    /// </summary>
    private static List<TimelineRow> LoadEmotes(IDataManager data, IPluginLog log)
    {
        var list = new List<TimelineRow>(256);
        try
        {
            var seen = new HashSet<uint>(256);
            foreach (var e in data.GetExcelSheet<Emote>())
            {
                var tid = e.ActionTimeline[0].RowId;       // standing/main animation; 0 = facial-only, skip
                if (tid == 0) continue;
                var name = e.Name.ExtractText();
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (!seen.Add(tid)) continue;              // one button per distinct clip
                var cmd = e.TextCommand.IsValid ? e.TextCommand.Value.Command.ExtractText() : "";
                var key = !string.IsNullOrEmpty(cmd) ? cmd : "emote/" + name.ToLowerInvariant().Replace(' ', '_');
                list.Add(new TimelineRow(tid, "emote", "", key, name));
            }
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            log.Information($"TimelineIndex: {list.Count} emotes from Emote sheet");
        }
        catch (Exception e)
        {
            log.Error(e, "TimelineIndex: failed to load emotes (Emotes group will be empty)");
        }
        return list;
    }

    /// <summary>This skeleton's catalogued specials (empty if none/unknown).</summary>
    public IReadOnlyList<TimelineRow> ForSkeleton(string skeletonCode)
        => skeletonCode.Length > 0 && _bySkel.TryGetValue(skeletonCode, out var list) ? list : Empty;

    /// <summary>True when this skeleton has an offline caps profile — i.e. a monster (mNNNN) or
    /// demihuman (dNNNN) whose resident .pap animation set we extracted. Human (cNNNN) and unknown
    /// skeletons return false and are NEVER dummy-filtered: a player body genuinely walks/emotes, and
    /// we have no physical oracle to prune it. The UI uses this to decide whether the Common list can
    /// be trimmed to what the model can actually play, or must stay the full browsable pile.</summary>
    public bool HasCaps(string skel) => skel.Length > 0 && _capsBySkel.ContainsKey(skel);

    /// <summary>
    /// The Common timelines this skeleton can ACTUALLY play, dummies removed. For a skeleton with an
    /// offline caps profile (<see cref="HasCaps"/>), keep only base-lane rows whose <see cref="Codebook"/>
    /// internal HAVOK name is present in that skeleton's resident .pap set, collapse the handful of
    /// key-variants that map to one internal name (keep the shortest, most canonical key), then sort by
    /// friendly name. For a human/unknown skeleton (no caps) return the full <see cref="Common"/> list
    /// unfiltered. This is the payoff of the caps extraction: a typical combat monster plays ~6 of the
    /// 400-plus Common rows, the rest being player-body dummies that would animate nothing.
    /// </summary>
    public IReadOnlyList<TimelineRow> ValidCommonFor(string skel)
    {
        if (_validCommonMemo.TryGetValue(skel, out var cached)) return cached;
        IReadOnlyList<TimelineRow> result;
        if (!_capsBySkel.TryGetValue(skel, out var caps))
            result = Common; // human/unknown: no filtering
        else
            result = Common
                .Where(r => Codebook.TryGetValue(r.Key, out var it) && caps.Contains(it))
                .GroupBy(r => Codebook[r.Key])
                .Select(g => g.OrderBy(r => r.Key.Length).ThenBy(r => r.Key, StringComparer.Ordinal).First())
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        _validCommonMemo[skel] = result;
        return result;
    }

    /// <summary>
    /// Resolve a Codebook <paramref name="key"/> (e.g. "battle/dead") to the ActionTimeline id to FIRE on the
    /// given skeleton, or 0 if it can't be played there. Three gates, each already trusted elsewhere: the key
    /// must be a known base-lane <see cref="Codebook"/> entry; the skeleton's caps must contain that entry's
    /// internal HAVOK name (the same physical oracle <see cref="ValidCommonFor"/> uses); and the key must exist
    /// in Common (its global id). This is the resolver the compound-gesture (Combos) buttons use for BOTH
    /// visibility and which ids to play — a 0 means "don't show / can't play", exactly like a caps-filtered
    /// Common row. Returns 0 for human/unknown skeletons (no caps profile), which is correct: the Combos are
    /// monster/demihuman terminal poses (dead_pose &amp;c.) whose existence is proven by the caps set.
    /// </summary>
    public uint ResolvePlayable(string skel, string key)
    {
        if (!Codebook.TryGetValue(key, out var internalName)) return 0;
        if (!_capsBySkel.TryGetValue(skel, out var caps) || !caps.Contains(internalName)) return 0;
        return _commonKeyToId.TryGetValue(key, out var id) ? id : 0;
    }

    /// <summary>
    /// Resolve a Codebook <paramref name="key"/> to its global Common ActionTimeline id WITHOUT the per-skeleton
    /// caps gate — the id to fire on a standard humanoid. This is the sibling <see cref="ResolvePlayable"/> can't
    /// serve for a human puppet: that method gates on <c>_capsBySkel</c>, which only profiles monster/demihuman
    /// resident .paps, so it returns 0 for a human (no caps entry) even though the human CAN play the shared
    /// Common clip. The per-puppet control surface tracks only a display label (not a skeleton code), and its use
    /// case is humanoid NPCs, so it wants exactly the Common human id here — read from the shipped index (single
    /// source of truth) rather than a hard-coded literal that would silently drift on a per-patch regen. Returns 0
    /// if the key isn't a Common row; on a non-humanoid puppet the Common id may not match its resident .pap
    /// (that needs the caps-gated path) — an accepted degradation for the humanoid-first puppet buttons.
    /// </summary>
    public uint ResolveCommonId(string key) => _commonKeyToId.TryGetValue(key, out var id) ? id : 0;

    /// <summary>
    /// Loads Data/special-names.csv (columns <c>Id,Name</c>) — the offline Action-sheet join mapping a
    /// monster special's ActionTimeline id to its real ability name (1094 -> "Firestorm"). A superset:
    /// only ids that actually appear as skeleton-special rows in timeline-index.csv are ever consulted,
    /// and a blank/missing name falls back to the "Special NNN" heuristic at row-load time.
    /// </summary>
    private void LoadSpecialNames(string path, IPluginLog log)
    {
        try
        {
            using var r = new StreamReader(path, Encoding.UTF8);
            r.ReadLine(); // header
            string? line;
            while ((line = r.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                var comma = line.IndexOf(',');
                if (comma <= 0) continue;
                if (!uint.TryParse(line.AsSpan(0, comma), out var id)) continue;
                var name = line[(comma + 1)..];
                // Tolerate a CSV-quoted name (an ability whose text contains a comma).
                if (name.Length >= 2 && name[0] == '"' && name[^1] == '"')
                    name = name[1..^1].Replace("\"\"", "\"");
                if (name.Length > 0) _specialNames[id] = name;
            }
            log.Information($"TimelineIndex: {_specialNames.Count} special skill-names from {path}");
        }
        catch (Exception e)
        {
            log.Error(e, $"TimelineIndex: failed to load {path} (specials keep heuristic names)");
        }
    }

    /// <summary>
    /// Loads Data/common-skill-names.csv (columns <c>Id,Name</c>) — the offline Action-sheet join
    /// (see <see cref="_commonSkillNames"/>) that relabels a shared Common cast-pose timeline id with
    /// the recognisable class skill that triggers it (11938 -> "Hammer Motif"). Same tolerant parse as
    /// <see cref="LoadSpecialNames"/>; a missing file just leaves Common rows on their key heuristic.
    /// </summary>
    private void LoadCommonSkillNames(string path, IPluginLog log)
    {
        try
        {
            using var r = new StreamReader(path, Encoding.UTF8);
            r.ReadLine(); // header
            string? line;
            while ((line = r.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                var comma = line.IndexOf(',');
                if (comma <= 0) continue;
                if (!uint.TryParse(line.AsSpan(0, comma), out var id)) continue;
                var name = line[(comma + 1)..];
                if (name.Length >= 2 && name[0] == '"' && name[^1] == '"')
                    name = name[1..^1].Replace("\"\"", "\"");
                if (name.Length > 0) _commonSkillNames[id] = name;
            }
            log.Information($"TimelineIndex: {_commonSkillNames.Count} common skill-names from {path}");
        }
        catch (Exception e)
        {
            log.Error(e, $"TimelineIndex: failed to load {path} (common rows keep heuristic names)");
        }
    }

    /// <summary>
    /// Loads Data/skel-anim-caps.csv (columns <c>Skel,McType,NumAnim,Names</c>) — the offline extraction
    /// of every monster/demihuman resident .pap's internal HAVOK animation-name table. <c>Names</c> is a
    /// space-joined set of internal names (cbbm_id0 cbbm_atk1 ...) and is the LAST field, so it never
    /// collides with the comma delimiter. This set is the physical oracle behind <see cref="ValidCommonFor"/>.
    /// </summary>
    private void LoadCaps(string path, IPluginLog log)
    {
        try
        {
            using var r = new StreamReader(path, Encoding.UTF8);
            r.ReadLine(); // header
            string? line;
            while ((line = r.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                // Names is space-joined (no internal commas), so a 4-way split keeps the whole set intact.
                var f = line.Split(',', 4);
                if (f.Length < 4) continue;
                var skel = f[0];
                if (skel.Length == 0) continue;
                var names = f[3].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (names.Length == 0) continue;
                _capsBySkel[skel] = new HashSet<string>(names, StringComparer.Ordinal);
            }
            log.Information($"TimelineIndex: {_capsBySkel.Count} skeleton caps from {path}");
        }
        catch (Exception e)
        {
            log.Error(e, $"TimelineIndex: failed to load {path} (Common list stays unfiltered)");
        }
    }

    /// <summary>Heuristic display name from a timeline key path. Explicitly lossy.</summary>
    private static string Prettify(string key)
    {
        if (key.Length == 0) return "(timeline)";

        // Monster specials. Two shapes reach here:
        //  - standalone mon_sp/mNNNN/mon_spNNN -> "Special NNN" (the digits).
        //  - generic resident-special triggers battle/mon_sp_a_loop / _start -> "Special A" / "Special A (intro)".
        var sp = key.IndexOf("/mon_sp", StringComparison.Ordinal);
        if (sp >= 0)
        {
            var tail = key[(key.LastIndexOf('/') + 1)..]; // mon_spNNN  OR  mon_sp_a_loop
            var digits = new string(tail.Where(char.IsDigit).ToArray());
            if (digits.Length > 0) return $"Special {digits}";
            var slot = SpecialSlot(tail);
            if (slot != '\0')
                return tail.EndsWith("_start", StringComparison.Ordinal)
                    ? $"Special {char.ToUpperInvariant(slot)} (intro)"
                    : $"Special {char.ToUpperInvariant(slot)}";
            return TitleWords(tail);
        }

        var parts = key.Split('/');
        var leaf = parts[^1];
        var top = parts[0];
        // Strip provenance noise the leading key path already carries, so the chip reads as the gesture, not
        // its data-folder: "hwd_fate_saw_wood_loop" -> "Saw Wood Loop", "event_action_wks4_end" -> "Cosmic 4
        // End". The provenance itself is not lost — it is exactly what the Animations tab's category chip
        // (Craft/Interact/…) now shows. Only the fallback name changes; CSV-named rows never reach here.
        foreach (var p in LeafPrefixes)
            if (leaf.Length > p.Length && leaf.StartsWith(p, StringComparison.Ordinal)) { leaf = leaf[p.Length..]; break; }
        if (leaf.Contains("wks", StringComparison.Ordinal))
            leaf = leaf.Replace("wks", "cosmic_", StringComparison.Ordinal);   // Cosmic Exploration crafting slots
        var name = TitleWords(leaf);
        // Keep the battle-stance context that lives in the top folder.
        return top.Equals("battle", StringComparison.OrdinalIgnoreCase) ? $"Battle {name}" : name;
    }

    /// <summary>Leading leaf tokens that only name a data folder/provenance, not the gesture — stripped by
    /// <see cref="Prettify"/> because the Animations-tab category chip now carries that provenance.</summary>
    private static readonly string[] LeafPrefixes =
        { "hwd_fate_", "wks_fate_", "event_action_", "event_item_", "u_" };

    /// <summary>Slot letter of a generic resident-special key tail ("mon_sp_a_loop" -> 'a',
    /// "p1_mon_sp_b_short_start" -> 'b'), else '\0' when the tail isn't a slot-lettered special.</summary>
    private static char SpecialSlot(string tail)
    {
        const string p = "mon_sp_";
        var i = tail.IndexOf(p, StringComparison.Ordinal);
        if (i < 0) return '\0';
        var j = i + p.Length;
        return j < tail.Length && char.IsLetter(tail[j]) && (j + 1 >= tail.Length || tail[j + 1] == '_')
            ? tail[j] : '\0';
    }

    /// <summary>"idle_inactive1" -> "Idle Inactive1"; "turn_loop_l" -> "Turn Loop L".</summary>
    private static string TitleWords(string token)
    {
        var words = token.Split('_', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            var w = words[i];
            words[i] = char.ToUpperInvariant(w[0]) + w[1..];
        }
        return string.Join(' ', words);
    }
}
