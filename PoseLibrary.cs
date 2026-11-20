using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Lumina.Excel.Sheets;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace HDM;

/// <summary>How a library entry plays. <see cref="Hold"/> = held base override (<c>AnimationService.Loop</c>),
/// <see cref="Once"/> = one-shot (<c>PlayOnce</c>), <see cref="IntroHold"/> = intro then held loop
/// (<c>PlaySequence</c>, <see cref="LibEntry.HoldId"/>).</summary>
public enum PosePlay { Once, Hold, IntroHold }

/// <summary>One named, playable library entry: a pose, gesture, warp or job skill. Ids stay internal (tooltip
/// only) — the DM picks by name and group, never by timeline id.</summary>
public sealed record LibEntry(string Name, PosePlay Play, ushort Id, ushort HoldId, string Key, string Note = "",
                              uint Icon = 0, HandProp? Prop = null);

/// <summary>A hand prop: a weapon-slot model (Set/Base/Variant) loaded into the main hand while posing — the way
/// the game's own cutscene props work. From Data/props.csv (Anamnesis, MIT).</summary>
public sealed record HandProp(string Name, ushort Set, ushort Base, ushort Variant);

/// <summary>A named group of entries ("Hurt & collapse", "Dragoon (DRG)").</summary>
public sealed record LibGroup(string Name, IReadOnlyList<LibEntry> Items, uint JobId = 0);

/// <summary>
/// The humanoid pose + job-skill library (b18), built LIVE from the game sheets so it tracks the installed
/// patch with no offline regen. Two catalogs:
///
/// <para><b>Poses.</b> The game's NPC/cutscene poses — every <c>event_base/*</c> (719) and <c>event/*</c>
/// (365) ActionTimeline row, none of which HDM's offline catalog carried — grouped by what they look like
/// (Hurt &amp; collapse, Lying, Kneeling, Sitting, Talking, …) with readable names, so the DM never deals in
/// ids. Curated entries come first in their group, with hand-written names and play modes taken from
/// Marionette 0.2.0.1 (MusicManBowls, GPL-3.0, a Brio fork): 11 hurt poses (held), 24 talk gestures
/// (held "through the line" or once "at the start"), and the warp in/out animations. The rest are grouped by
/// keyword; <c>event_base</c> poses are base poses and hold, <c>event</c> poses play once, and a
/// <c>_start</c>/<c>_loop</c> pair plays intro-then-hold.</para>
///
/// <para><b>Per-body gate.</b> Event poses exist only for some bodies, so each entry is offered only when its
/// .pap exists for the subject's body: <c>chara/human/c{body}/animation/a0001/bt_common/{key}.pap</c>, else the
/// Hyur-male (c0101) file — except child bodies (cNN04), which get no fallback. Body = the drawn
/// <c>Human.RaceSexId</c>, so a glamoured/guised actor is checked against what it actually renders as.
/// Memoized per (body, timeline).</para>
///
/// <para><b>Job skills.</b> Player actions (non-PvP, with an animation) grouped by job, the skill's own
/// <c>AnimationEnd</c> timeline (its VFX ride inside that timeline). Base-class skills fold into their jobs
/// (a Lancer skill lists under Dragoon; an Arcanist skill under both Summoner and Scholar). Jobs in the game's
/// own order (ClassJob.UIPriority), skills by level.</para>
/// </summary>
public sealed class PoseLibrary
{
    private readonly IDataManager _data;
    private readonly Dictionary<ushort, string> _keyById = new(2048);
    private readonly Dictionary<(int Body, ushort Id), bool> _hasMemo = new(4096);
    private readonly Dictionary<int, IReadOnlyList<LibGroup>> _posesByBody = new(8);

    /// <summary>All pose groups, unfiltered (the per-body view is <see cref="PosesFor"/>).</summary>
    public IReadOnlyList<LibGroup> Poses { get; }

    /// <summary>Job-skill groups, one per job.</summary>
    public IReadOnlyList<LibGroup> JobSkills { get; }

    /// <summary>Every hand prop (Data/props.csv), sorted by name — the "In hand" picker's list.</summary>
    public IReadOnlyList<HandProp> Props { get; }

    public PoseLibrary(IDataManager data, string dataDir, IPluginLog log)
    {
        _data = data;
        Poses = Array.Empty<LibGroup>();
        JobSkills = Array.Empty<LibGroup>();
        Props = LoadProps(System.IO.Path.Combine(dataDir, "props.csv"), log);
        try
        {
            foreach (var row in data.GetExcelSheet<ActionTimeline>())
            {
                var k = row.Key.ExtractText();
                if (k.Length > 0 && row.RowId <= ushort.MaxValue) _keyById[(ushort)row.RowId] = k;
            }
            Poses = AttachAutoProps(BuildPoses());
            log.Information($"PoseLibrary: {Poses.Sum(g => g.Items.Count)} poses in {Poses.Count} groups, " +
                            $"{Poses.Sum(g => g.Items.Count(e => e.Prop != null))} with an auto-prop.");
        }
        catch (Exception e) { log.Error(e, "PoseLibrary: pose catalog failed to build."); }
        try
        {
            JobSkills = BuildJobSkills(data);
            log.Information($"PoseLibrary: {JobSkills.Sum(g => g.Items.Count)} job skills in {JobSkills.Count} jobs.");
        }
        catch (Exception e) { log.Error(e, "PoseLibrary: job-skill catalog failed to build."); }
    }

    // ---- curated (Marionette 0.2.0.1) -------------------------------------------------------------------

    private static readonly (ushort Id, string Name)[] HurtPoses =
    [
        (4204, "Hurt, standing"), (5673, "Hurt, standing (other side)"), (733, "Staggering"),
        (9095, "Down on one knee"), (4222, "Clutching the chest"), (4206, "Hurt, on the ground"),
        (824, "Exhausted on the ground"), (5598, "Crawling"), (4324, "Lying in pain"),
        (4208, "Lying, suffering"), (4202, "Crying on the ground"),
    ];

    // Loops = true: holds "all through the line"; false: plays "once at the start".
    private static readonly (ushort Id, string Name, bool Loops)[] TalkGestures =
    [
        (799, "Standing talk", true), (5518, "Standing talk 2", true), (5608, "Standing talk 3", true),
        (9039, "Thinking as they talk", true), (9029, "Worried talk", true), (796, "Making a speech", true),
        (763, "Talk", false), (764, "Talk 2", false), (5695, "Talk 3", false), (5696, "Talk 4", false),
        (9196, "Talk 5", false), (9232, "Talk 6", false), (4218, "One hand", false), (4219, "Both hands", false),
        (950, "Big gesture", false), (9233, "Big gesture 2", false), (951, "Pleading", false),
        (933, "Raised finger", false), (9015, "Raised finger 2", false), (4194, "Pointing", false),
        (768, "Angry", false), (4220, "Joking", false), (5627, "Nodding yes", false), (4236, "Shaking no", false),
    ];

    private static readonly (ushort Id, string Name)[] Warps =
    [
        (7740, "Fade out"), (7750, "Fade in"), (7739, "Blink out"), (7749, "Blink in"),
        (5643, "Light warp"), (5644, "Ninja vanish"), (6192, "Forced warp"), (8488, "Return"),
    ];

    // Keyword groups for the uncurated event poses, first match wins (order matters: "kneel_pray" is Prayer,
    // "ground_tired" is Hurt). Matched against the key stem without its event_/event_base_ prefix.
    private static readonly (string Group, Regex Pattern)[] Keywords =
    [
        ("Hurt & collapse", new(@"damage|pain|hurt|injur|suffer|collapse|faint|dying|dead|ded$|stagger|crawl|exhaust|wound|bleed|tired|sick|dizzy|knockdown|fall|explosion|disappear")),
        ("Lying", new(@"lie|lying|sleep|nap|lay|bed")),
        ("Prayer & ceremony", new(@"pray|worship|bow|salute|ceremony|ritual|oath|faith|bless|spirit|muse|healing")),
        ("Kneeling", new(@"kneel|knee|seiza")),
        ("Sitting", new(@"sit|chair|seat|bench|ground|floor|agura")),
        ("Crouching", new(@"crouch|squat|stoop|headdown")),
        ("Sorrow", new(@"cry|sad|sob|weep|grief|mourn|tear|regret|sorry|depress|orz")),
        ("Fear", new(@"fear|scare|cower|shiver|tremble|panic|cold|startle|frighten|escape|captured|bind")),
        ("Anger & provoking", new(@"angry|anger|rage|furious|threat|annoy|provoke|come_on|menace|brawl")),
        ("Joy", new(@"laugh|joy|happy|cheer|clap|smile|celebrat|toast|excited|appreciat")),
        ("Performing", new(@"dance|sing|harp|perform|play|appeal|sexy|cardgame")),
        ("Talking", new(@"talk|speech|speak|explain|guide|call|shout|coach|direction|announce|lecture|order")),
        ("Thinking & worry", new(@"think|ponder|worr|trouble|confus|question|doubt|look|search|check|review|read|study|listen|wonder|watch|fidget|mysterious")),
        ("Reactions", new(@"reaction|shock|surprise|nod|yes|_no$|^no$|please|thank|greet|wave|attention|agree|deny|add_|point|beckon|hand|byebye|give")),
        ("Eating & drinking", new(@"eat|drink|tea|food|meal|cup|bottle|sake|tobacco|glass")),
        ("Crafting & chores", new(@"work|carry|dig|hammer|craft|cook|chef|write|memo|sweep|scrub|box|lift|pull|push|fish|angler|chop|leather|carpenter|smith|alchem|weav|tailor|grass|water|manure|plow|brush|shine|sprinkle|harvest|clean|wash|repair|build|saw|paint|sew|forge|item|rack|shop|waiter|sell|serve|deliver|count|money|ledger|mineral|mat_")),
        ("Animals & mounts", new(@"chocobo|horse|cat|dog|bird|pet|feed|ride")),
        ("Battle & weapons", new(@"battle|guard|stance|ready|draw|sword|spear|bow|yumi|axe|dagger|attack|fight|train|swing|shoot|magic|cast|aim|shield|warming_up|harpoon|arms|archer|colosseum|rogue")),
        ("Walking", new(@"walk|run|step|stroll|wander|jump|bound")),
        ("Standing & idle", new(@"stand|idle|wait|wall|lean|rest|pose|base|comfortable|old_person")),
    ];

    private static readonly Regex Suffix = new(@"_(start|loop|end|st|lp|ed)$");

    private IReadOnlyList<LibGroup> BuildPoses()
    {
        var groups = new Dictionary<string, List<LibEntry>>();
        var used = new HashSet<ushort>();
        void Add(string g, LibEntry e)
        {
            if (!groups.TryGetValue(g, out var l)) groups[g] = l = new List<LibEntry>();
            l.Add(e);
            used.Add(e.Id);
            if (e.HoldId != 0) used.Add(e.HoldId);
        }

        foreach (var (id, name) in HurtPoses)
            if (_keyById.TryGetValue(id, out var k)) Add("Hurt & collapse", new(name, PosePlay.Hold, id, 0, k));
        foreach (var (id, name, loops) in TalkGestures)
            if (_keyById.TryGetValue(id, out var k))
                Add("Talking", new(name, loops ? PosePlay.Hold : PosePlay.Once, id, 0, k,
                    loops ? "Holds all through the line." : "Plays once, at the start of a line."));
        foreach (var (id, name) in Warps)
            if (_keyById.TryGetValue(id, out var k)) Add("Warp in & out", new(name, PosePlay.Once, id, 0, k,
                "An entrance/exit animation; move or spawn the puppet around it."));

        // Uncurated event poses. Pair _start with its _loop (intro -> hold); a lone _loop holds; _end rows are the
        // release half of a pair and are not offered on their own.
        var byStem = new Dictionary<string, (ushort Start, ushort Loop, ushort Plain, bool Base)>();
        foreach (var (id, key) in _keyById)
        {
            var slash = key.IndexOf('/');
            if (slash < 0) continue;
            var lane = key[..slash];
            if (lane is not ("event" or "event_base")) continue;
            var leaf = key[(slash + 1)..];
            var m = Suffix.Match(leaf);
            var stem = m.Success ? leaf[..m.Index] : leaf;
            byStem.TryGetValue(stem, out var s);
            s.Base |= lane == "event_base";
            switch (m.Success ? m.Groups[1].Value : "")
            {
                case "start" or "st": s.Start = s.Start == 0 ? id : s.Start; break;
                case "loop" or "lp": s.Loop = s.Loop == 0 ? id : s.Loop; break;
                case "end" or "ed": break;
                default: s.Plain = s.Plain == 0 ? id : Math.Min(s.Plain, id); break;
            }
            byStem[stem] = s;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in groups.Values) foreach (var e in g) names.Add(e.Name);
        foreach (var (stem, s) in byStem.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            LibEntry e;
            var name = Pretty(stem);
            if (s.Start != 0 && s.Loop != 0) e = new(name, PosePlay.IntroHold, s.Start, s.Loop, _keyById[s.Loop]);
            else if (s.Loop != 0) e = new(name, PosePlay.Hold, s.Loop, 0, _keyById[s.Loop]);
            else if (s.Plain != 0) e = new(name, s.Base ? PosePlay.Hold : PosePlay.Once, s.Plain, 0, _keyById[s.Plain]);
            else if (s.Start != 0) e = new(name, PosePlay.Once, s.Start, 0, _keyById[s.Start]);
            else continue;
            if (used.Contains(e.Id)) continue;
            var unique = name;
            for (var n = 2; !names.Add(unique); n++) unique = $"{name} ({n})";
            if (unique != name) e = e with { Name = unique };
            var bare = Regex.Replace(stem, @"^event_(base_)?", "");
            var group = Keywords.FirstOrDefault(kw => kw.Pattern.IsMatch(bare)).Group ?? "More";
            Add(group, e);
        }

        var order = new List<string> { "Hurt & collapse", "Warp in & out" };
        order.AddRange(Keywords.Select(k => k.Group).Where(g => !order.Contains(g)));
        order.Add("More");
        return order.Where(groups.ContainsKey)
            .Select(g => new LibGroup(g, groups[g]
                .OrderBy(e => IsCurated(e.Id) ? 0 : 1)
                .ThenBy(e => IsCurated(e.Id) ? CuratedRank(e.Id) : 0)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList()))
            .ToList();
    }

    private static bool IsCurated(ushort id) => CuratedRank(id) < int.MaxValue;
    private static int CuratedRank(ushort id)
    {
        var i = Array.FindIndex(HurtPoses, p => p.Id == id); if (i >= 0) return i;
        i = Array.FindIndex(TalkGestures, p => p.Id == id); if (i >= 0) return 100 + i;
        i = Array.FindIndex(Warps, p => p.Id == id); if (i >= 0) return 200 + i;
        return int.MaxValue;
    }

    /// <summary>"event_base_stand_listen" -> "Stand listen"; "event_base_idle2" -> "Idle 2".</summary>
    private static string Pretty(string stem)
    {
        var s = Regex.Replace(stem, @"^event_(base_)?", "");
        s = Regex.Replace(s, @"(?<=[a-z])(?=\d)", " ").Replace('_', ' ').Trim();
        return s.Length == 0 ? stem : MobRow.Cap(s);
    }

    // ---- per-body gate ----------------------------------------------------------------------------------

    /// <summary>The subject's drawn human body (<c>Human.RaceSexId</c>, e.g. 101, 1801), or 0 when it isn't
    /// drawn as a human (then no per-body filtering is applied).</summary>
    public static unsafe int BodyOf(ICharacter? chara)
    {
        if (chara == null || chara.Address == nint.Zero) return 0;
        var ch = (NativeCharacter*)chara.Address;
        var draw = (CharacterBase*)ch->GameObject.DrawObject;
        if (draw == null || draw->GetModelType() != CharacterBase.ModelType.Human) return 0;
        return ((Human*)draw)->RaceSexId;
    }

    /// <summary>True when timeline <paramref name="id"/> has an animation for this body (Marionette's check).</summary>
    public bool HasAnimation(int body, ushort id)
    {
        if (body <= 0 || !_keyById.TryGetValue(id, out var key)) return true;
        if (_hasMemo.TryGetValue((body, id), out var ok)) return ok;
        ok = _data.FileExists($"chara/human/c{body:D4}/animation/a0001/bt_common/{key}.pap")
             || (body % 100 != 4 && _data.FileExists($"chara/human/c0101/animation/a0001/bt_common/{key}.pap"));
        _hasMemo[(body, id)] = ok;
        return ok;
    }

    /// <summary>Pose groups trimmed to what this body can play (empty groups dropped). Memoized per body.</summary>
    public IReadOnlyList<LibGroup> PosesFor(int body)
    {
        if (body <= 0) return Poses;
        if (_posesByBody.TryGetValue(body, out var hit)) return hit;
        var result = Poses
            .Select(g => new LibGroup(g.Name, g.Items.Where(e => HasAnimation(body, e.Id) && (e.HoldId == 0 || HasAnimation(body, e.HoldId))).ToList()))
            .Where(g => g.Items.Count > 0).ToList();
        _posesByBody[body] = result;
        return result;
    }

    // ---- job skills --------------------------------------------------------------------------------------

    private static IReadOnlyList<LibGroup> BuildJobSkills(IDataManager data)
    {
        var jobs = data.GetExcelSheet<ClassJob>();
        // class -> its jobs (a job's ClassJobParent is its base class; a class is its own parent).
        var children = new Dictionary<uint, List<uint>>();
        foreach (var j in jobs)
        {
            var parent = j.ClassJobParent.RowId;
            if (j.RowId == 0 || parent == 0 || parent == j.RowId) continue;
            if (!children.TryGetValue(parent, out var l)) children[parent] = l = new List<uint>();
            l.Add(j.RowId);
        }

        var byJob = new Dictionary<uint, Dictionary<string, (byte Level, LibEntry Entry)>>();
        foreach (var a in data.GetExcelSheet<Lumina.Excel.Sheets.Action>())
        {
            if (!a.IsPlayerAction || a.IsPvP || a.AnimationEnd.RowId == 0 || a.AnimationEnd.RowId > ushort.MaxValue) continue;
            var name = a.Name.ExtractText().Trim();
            var cj = a.ClassJob.RowId;
            if (name.Length == 0 || cj == 0) continue;
            var key = a.AnimationEnd.ValueNullable?.Key.ExtractText() ?? "";
            var entry = new LibEntry(name, PosePlay.Once, (ushort)a.AnimationEnd.RowId, 0, key, $"Level {a.ClassJobLevel}", a.Icon);
            foreach (var job in children.TryGetValue(cj, out var kids) ? kids : [cj])
            {
                if (!byJob.TryGetValue(job, out var d)) byJob[job] = d = new Dictionary<string, (byte, LibEntry)>(StringComparer.OrdinalIgnoreCase);
                d.TryAdd(name, (a.ClassJobLevel, entry));
            }
        }

        return byJob
            .Select(kv => (Job: jobs.GetRowOrDefault(kv.Key), Skills: kv.Value.Values))
            .Where(x => x.Job is not null)
            .OrderBy(x => x.Job!.Value.UIPriority).ThenBy(x => x.Job!.Value.RowId)
            .Select(x => new LibGroup(
                $"{MobRow.Cap(x.Job!.Value.Name.ExtractText())} ({x.Job!.Value.Abbreviation.ExtractText()})",
                x.Skills.OrderBy(s => s.Level).ThenBy(s => s.Entry.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(s => s.Entry).ToList(), x.Job!.Value.RowId))
            .ToList();
    }

    // ---- hand props ---------------------------------------------------------------------------------------

    private readonly Dictionary<string, HandProp> _propByName = new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<HandProp> LoadProps(string path, IPluginLog log)
    {
        var list = new List<HandProp>(800);
        try
        {
            foreach (var line in System.IO.File.ReadLines(path))
            {
                if (line.Length == 0 || line[0] == '#' || line.StartsWith("Name,", StringComparison.Ordinal)) continue;
                // Name may be quoted and contain commas; the last three fields are numeric.
                var c3 = line.LastIndexOf(','); var c2 = line.LastIndexOf(',', c3 - 1); var c1 = line.LastIndexOf(',', c2 - 1);
                if (c1 <= 0) continue;
                if (!ushort.TryParse(line.AsSpan(c1 + 1, c2 - c1 - 1), out var s) ||
                    !ushort.TryParse(line.AsSpan(c2 + 1, c3 - c2 - 1), out var b) ||
                    !ushort.TryParse(line.AsSpan(c3 + 1), out var v)) continue;
                var name = line[..c1].Trim();
                if (name.Length > 1 && name[0] == '"') name = name[1..^1].Replace("\"\"", "\"");
                var p = new HandProp(name, s, b, v);
                list.Add(p);
                _propByName.TryAdd(name, p);
            }
            log.Information($"PoseLibrary: {list.Count} hand props from {path}");
        }
        catch (Exception e) { log.Warning($"PoseLibrary: no hand props ({e.Message})."); }
        return list;
    }

    // Auto-prop rules: a WORD in the pose's key (split on '_' and digits) -> a named prop. First rule wins, so the
    // specific drinks precede the generic "drink". Whole words only: "teach" never gets a tea cup.
    private static readonly (string[] Words, string Prop)[] AutoPropRules =
    [
        (["tea"], "Tea Cup"), (["sake"], "Sake Cup"), (["wine"], "Wine Cup"),
        (["beer", "ale", "tankard", "beerhall"], "Tankard of Ale"),
        (["drink", "hotdrink", "drunk", "tumbler", "cup"], "Cup"),
        (["tobacco", "smoke", "smoking", "pipe"], "White Smoking Pipe"),
        (["letter", "note"], "Letter or note"), (["scroll"], "Hingan Scroll"),
        (["book", "read", "reading", "study", "tome"], "Open Book"),
        (["write", "writing", "memo", "quill", "pen"], "Feather Quill"),
        (["sweep", "broom"], "Broom"),
        (["fan"], "Japanese Fan"), (["umbrella"], "Red Japanese Umbrella"), (["lantern"], "Lantern"),
        (["flower", "bouquet"], "Bouquet of Flowers"), (["bottle"], "Wine Bottle"),
    ];

    private IReadOnlyList<LibGroup> AttachAutoProps(IReadOnlyList<LibGroup> groups)
    {
        if (_propByName.Count == 0) return groups;
        HandProp? For(string key)
        {
            var leaf = key[(key.IndexOf('/') + 1)..];
            var words = Regex.Split(leaf, @"[_\d]+").Where(w => w.Length > 0).ToHashSet(StringComparer.Ordinal);
            foreach (var (ws, prop) in AutoPropRules)
                if (ws.Any(words.Contains) && _propByName.TryGetValue(prop, out var p)) return p;
            return null;
        }
        return groups.Select(g => g with { Items = g.Items.Select(e => For(e.Key) is { } p ? e with { Prop = p } : e).ToList() }).ToList();
    }
}
