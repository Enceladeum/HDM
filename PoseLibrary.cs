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
public sealed record HandProp(string Name, ushort Set, ushort Base, ushort Variant, bool OffHand = false);

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

    private readonly IPluginLog? _log;

    public PoseLibrary(IDataManager data, string dataDir, IPluginLog log)
    {
        _data = data;
        _log = log;
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
            _npcProps = BuildNpcProps(data, log);   // b57: the game's own pose -> prop pairing
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

    // Keyword groups for the uncurated event poses, first match wins (order matters: a chair pose is "On a chair"
    // before anything else; "ground_tired" is Hurt before "On the ground"). b27: matched against WHOLE WORDS of the stem
    // (split on '_' and digits), not substrings, so "bread" is no longer "read" and "paint" is no longer "pain".
    // Token forms: "x" = a word starting with x; "x$" = a word that IS x; "a_b" = that phrase in the stem.
    // Validated offline against all 1,084 event keys.
    private static readonly (string Group, string[] Tokens)[] Keywords =
    [
        ("On a chair", "chair seat bench stool sofa box_sit handrail_sit katana_chair_sit".Split(' ')),
        ("Hurt & collapse", "damage pain$ painful hurt injur suffer collapse faint dying dead ded$ stagger crawl exhaust wound bleed tired sick dizzy knockdown fall blowaway explosion groggy vomit nausea shudder chest_pain hold_head".Split(' ')),
        ("Lying", "lie$ lie lying sleep nap$ nap snooze lay$ bed$ float".Split(' ')),
        ("Prayer & ceremony", "pray worship salute ceremony ritual oath bless healing jp_bow gentleman_bow greeting_respect".Split(' ')),
        ("Kneeling", "kneel knee seiza both_knees".Split(' ')),
        ("On the ground", "sit$ sit ground floor agura stroke_sit rock_sit peep_sit".Split(' ')),
        ("Crouching", "crouch squat stoop headdown hang_body hang_down hangdman".Split(' ')),
        ("Sorrow", "cry$ sad sob weep grief mourn tear regret sorry depress orz sulk sigh disappoint".Split(' ')),
        ("Fear", "fear scare cower shiver tremble panic cold$ startle frighten escape captured bind jail hand_cold".Split(' ')),
        ("Anger & provoking", "angry anger rage furious threat annoy provoke come_on menace brawl catfight".Split(' ')),
        ("Joy", "laugh joy happy cheer clap smile celebrat celeb toast excited appreciat confetti special_joy gal_peace skillget soulget relicget zodiacget kettleget spirit".Split(' ')),
        ("Performing", "dance danc sing$ song harp$ bowharp perform play appeal sexy flute drum lute piano morin psyllium pirouette".Split(' ')),
        ("Eating & drinking", "eat drink tea$ food meal cup$ bottle sake tobacco glass_set hotdrink tequila juice soup mug tumbler alcohol drunk drunker bite".Split(' ')),
        ("Talking", "talk speech speak explain guide call shout coach direction announce lecture order teach strategy_meeting school_lesson".Split(' ')),
        ("Battle & weapons", "battle guard stance sword spear bow$ bow_action bow_repair minstrel_bow yumi axe dagger attack fight fighter swing shoot magic cast shield warming_up harpoon archer colosseum rogue skirmish grapple sasumata katana gun gunblade weapon mock_battle samurai soldier ninja gladiator snipe practice_swing bandit chaser flamethrower gravity_beam tractor_beam verthunder arcanist astrologian machinist divination".Split(' ')),
        ("Crafting & chores", "work working carry dig diggy hammer sledgehammer craft cook$ cooking chef write memo sweep broom scrub box lift fish fishing angler chop woodchop leather carpenter smith alchemist weav weaving tailor grass water manure plow brush shine sprinkle harvest clean wash washing wipe repair build saw paint spray sew forge rack shop waiter sell serve deliver count$ box_count money ledger mineral mat_remove maintenance drill welding vacuum machine panel_operation rammer cannon knead bake make mowing reaping salt sculptor shipbuilder sharpen snow_removal scoop milking sericulture plane$ kitchen dust_off draw_picture lantern_drawing growth_promoter feeding gather scavenge pick_up brick farmer coffee fitting bike check_item".Split(' ')),
        ("Thinking & worry", "think ponder worr trouble confus question doubt look search review read$ read reading study listen wonder watch fidget mysterious observe peep inspiration perceive take_look onlooker uneasy caution muse".Split(' ')),
        ("Reactions", "reaction shock surprise nod yes$ no$ please thank greet greeting wave attention agree deny add_ point beckon hand$ byebye give welcome blushing shy myself oneself finger_snap scratch touch_cheeks comfort$ cling protect rescue hold_out baby".Split(' ')),
        ("Animals & mounts", "chocobo horse cat$ dog bird pet$ feed alpaca punuty ride riding mount".Split(' ')),
        ("Walking", "walk run$ step stroll wander jump bound fly_away".Split(' ')),
        ("Standing & idle", "stand idle wait wall lean rest pose base comfortable old_person cute_stand arms$ adjust equip mask gird lipstick necklace stretch yawn breath inhale exhale sniff waist rubbing_hands table_elbow push_up pull_up".Split(' ')),
    ];

    // Rows never offered: low-poly/test/vfx/no-sound duplicates, mount/monster-only and race-variant debris.
    private static readonly Regex Junk = new(@"lowpoly|^test$|^test_mount$|vfx|no_se|^m\d{4}_|buddychocobo|^id\d|_practice$");

    private static bool TokenMatch(string tok, string[] words, string stem)
    {
        if (tok.Contains('_')) return ("_" + stem + "_").Contains("_" + tok + "_") || ("_" + stem).Contains("_" + tok);
        if (tok.EndsWith('$')) return words.Contains(tok[..^1]);
        return words.Any(w => w.StartsWith(tok, StringComparison.Ordinal));
    }

    /// <summary>The library group for a bare event stem ("stand_eat_bread_yum" -> "Eating &amp; drinking").</summary>
    public static string GroupOf(string stem)
    {
        var words = Regex.Split(stem, @"[_\d]+").Where(w => w.Length > 0).ToArray();
        foreach (var (g, toks) in Keywords)
            if (toks.Any(t => TokenMatch(t, words, stem))) return g;
        return "More";
    }

    /// <summary>The order groups appear in (rail + "All poses"): postures first, then moods, then activities.</summary>
    public static readonly string[] GroupOrder =
    [
        "Hurt & collapse", "On a chair", "On the ground", "Kneeling", "Lying", "Crouching", "Standing & idle", "Walking",
        "Talking", "Thinking & worry", "Reactions", "Joy", "Sorrow", "Fear", "Anger & provoking", "Prayer & ceremony",
        "Performing", "Eating & drinking", "Crafting & chores", "Battle & weapons", "Animals & mounts", "Warp in & out", "More",
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
            if (Junk.IsMatch(bare)) continue;
            Add(GroupOf(bare), e);
        }

        return GroupOrder.Where(groups.ContainsKey)
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

    /// <summary>b28: how long a timeline runs, in seconds, from its .tmb header (TMDH frame count at 30 fps); 0 if
    /// unreadable. Used to freeze a one-shot on its last frame ("Hold the ending").</summary>
    public float ClipSeconds(LibEntry e) => ClipSeconds(e.Id);

    /// <summary>b34: the same, by timeline id (a peer's held ending arrives as an id).</summary>
    public float ClipSeconds(ushort id)
    {
        if (_clipSeconds.TryGetValue(id, out var cached)) return cached;
        if (!_keyById.TryGetValue(id, out var key)) return 0f;
        var secs = 0f;
        try
        {
            var f = _data.GetFile($"chara/action/{key}.tmb");
            var d = f?.Data;
            // TMLB header (12 bytes), then TMDH: magic(4) size(4) id(2) unk(2) duration(2, frames) unk(2).
            if (d is { Length: >= 0x1C } && d[0x0C] == (byte)'T' && d[0x0D] == (byte)'M' && d[0x0E] == (byte)'D' && d[0x0F] == (byte)'H')
                secs = BitConverter.ToUInt16(d, 0x18) / 30f;
        }
        catch { /* unreadable: caller falls back to plain once */ }
        _clipSeconds[id] = secs;
        return secs;
    }
    private readonly Dictionary<ushort, float> _clipSeconds = new();

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
        // b31: the body's OWN file only. Proven in game (adjust cap, ground talk, ground rest on Midlander ♀ / Au Ra ♀):
        // the game does not fall back to another body for these bt_common event paps. UniversalPoses lifts that.
        ok = _data.FileExists($"chara/human/c{body:D4}/animation/a0001/bt_common/{key}.pap");
        _hasMemo[(body, id)] = ok;
        return ok;
    }

    /// <summary>b30: the body followed by its fallback bodies, depth first, read from each skeleton's .sklb header
    /// (u32 own id at 0x14, then up to four u32 parent ids at 0x18, 0xFFFFFFFF = none). Memoized.</summary>
    public IReadOnlyList<int> BodyChain(int body)
    {
        if (_chains.TryGetValue(body, out var hit)) return hit;
        var chain = new List<int>();
        void Walk(int b)
        {
            if (b <= 0 || chain.Contains(b) || chain.Count > 8) return;
            chain.Add(b);
            try
            {
                var d = _data.GetFile($"chara/human/c{b:D4}/skeleton/base/b0001/skl_c{b:D4}b0001.sklb")?.Data;
                if (d is not { Length: >= 0x28 } || BitConverter.ToUInt32(d, 0x14) != (uint)b) return;
                for (var o = 0x18; o < 0x28; o += 4)
                {
                    var parent = BitConverter.ToUInt32(d, o);
                    if (parent != uint.MaxValue) Walk((int)parent);
                }
            }
            catch { /* unreadable skeleton: own body only */ }
        }
        Walk(body);
        _chains[body] = chain;
        return chain;
    }
    private readonly Dictionary<int, IReadOnlyList<int>> _chains = new();

    /// <summary>Pose groups trimmed to what this body can play (empty groups dropped). Memoized per body.</summary>
    /// <summary>b31: true while UniversalPoses ports missing animations, so every body is offered every pose.</summary>
    public bool Universal
    {
        get => _universal;
        set { _universal = value; _posesByBody.Clear(); }
    }
    private bool _universal;

    /// <summary>The live porter (null until Plugin wires it), for the "plays natively on" info.</summary>
    public UniversalPoses? Porter { get; set; }

    /// <summary>b33: every event / event_base ActionTimeline key (what UniversalPoses ports).</summary>
    public IReadOnlyList<string> EventKeys => _eventKeys ??= _keyById.Values
        .Where(k => k.StartsWith("event/", StringComparison.Ordinal) || k.StartsWith("event_base/", StringComparison.Ordinal))
        .Distinct().ToList();
    private List<string>? _eventKeys;

    private static readonly int[] AdultBodies =
        [101, 201, 301, 401, 501, 601, 701, 801, 901, 1001, 1101, 1201, 1301, 1401, 1501, 1601, 1701, 1801];

    /// <summary>b42: does this timeline's animation spawn a visual effect of its own (a C012 node pointing at an .avfx
    /// inside the pap)? Such effects outlive an interrupted clip (touch ele shock's lightning stayed until a map change),
    /// so AnimationService redraws the actor when it releases one. Read once per id from the first body that ships it.</summary>
    public bool HasEffect(ushort id)
    {
        if (_hasEffect.TryGetValue(id, out var hit)) return hit;
        var yes = false;
        if (_keyById.TryGetValue(id, out var key))
            foreach (var b in AdultBodies)
            {
                var d = _data.GetFile($"chara/human/c{b:D4}/animation/a0001/bt_common/{key}.pap")?.Data;
                if (d == null) continue;
                yes = Contains(d, "C012"u8) && Contains(d, ".avfx"u8);
                break;
            }
        _hasEffect[id] = yes;
        return yes;
    }
    private readonly Dictionary<ushort, bool> _hasEffect = new();
    private static bool Contains(byte[] hay, ReadOnlySpan<byte> needle) => hay.AsSpan().IndexOf(needle) >= 0;

    public IReadOnlyList<LibGroup> PosesFor(int body)
    {
        if (body <= 0) return Poses;
        if (_universal && Porter is { Active: true } porter)
        {
            // b42: this body's own poses plus those a same-build body can lend it (see UniversalPoses.BuildGroup).
            if (_posesByBody.TryGetValue(-body, out var all)) return all;
            bool Ok(ushort id) => !_keyById.TryGetValue(id, out var k) || HasAnimation(body, id) || porter.DonorFor(body, k) > 0;
            var u = Poses.Select(g => new LibGroup(g.Name, g.Items.Where(e => Ok(e.Id) && (e.HoldId == 0 || Ok(e.HoldId))).ToList()))
                         .Where(g => g.Items.Count > 0).ToList();
            _posesByBody[-body] = u;
            return u;
        }
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

    // Auto-props (b27, combed against all 1,084 event keys). First an exact-stem override (stem = the key leaf without
    // event_/event_base_ and _start/_loop), then word rules: a WORD of the stem -> a named prop, first rule wins, so
    // specific drinks precede the generic "drink". Whole words only: "teach" never gets a tea cup. "" = keep the hand
    // empty (a carry pose's load is two-handed and would sit wrong in one hand).
    private static readonly Dictionary<string, string> PropOverrides = new(StringComparer.Ordinal)
    {
        // b29, from the game's own pairing (what the world NPCs standing in these poses actually hold, from their sheets):
        //   stand_read: book w9002 b1 v1 in the OFF hand (69 NPCs; 23 main) - the main-hand book was why it sat backwards.
        //   stand_with_book, chair_read: the same book, main hand. arcanist_reading: an Arcanist book weapon, main hand.
        //   stand_read2 has no NPC or quest using it; it takes stand_read's pairing.
        ["stand_read"] = "Thick Book 1|off", ["stand_read2"] = "Thick Book 1|off",
        ["stand_with_book"] = "Thick Book 1", ["chair_read"] = "Thick Book 1",
        ["arcanist_reading"] = "w1709 b1 v1=Arcanist grimoire",
        ["look_front_glasses"] = "Opera Glasses", ["look_up_glasses"] = "Opera Glasses",
        ["look_front_scope"] = "Spyglass", ["look_up_scope"] = "Spyglass", ["look_down_scope"] = "Spyglass",
        ["stand_memo_brush"] = "Hingan Brush", ["stand_memo_up_brush"] = "Hingan Brush",
        ["seiza_memo_brush"] = "Hingan Brush", ["talk_memo_brush"] = "Hingan Brush",
        ["chair_drink_hotcoffee"] = "Coffee Cup",
        ["chair_tropical_juice"] = "Glass w/ Orange Liquid and Straw", ["stand_tropical_juice"] = "Glass w/ Orange Liquid and Straw",
        ["sit_drink_tequila"] = "Corked Alcohol Bottle", ["stand_talk_tequila"] = "Corked Alcohol Bottle",
        ["sit_drink_soup"] = "Bowl of Soup (Orange)", ["eat_rice"] = "Bowl of Rice", ["eat_sushi"] = "Salmon Sushi",
        ["eat_riceball"] = "Onigiri", ["seiza_eat_riceball"] = "Onigiri", ["eat_cake"] = "Cake Slice",
        ["eat_ice_cream"] = "Multi Scoop Ice Cream Cone", ["sell_ice_cream"] = "Multi Scoop Ice Cream Cone",
        ["eat_cookie"] = "Jack Cookie", ["bt_cookie_gaia"] = "Jack Cookie", ["bt_cookie_ryne"] = "Jack Cookie",
        ["eat_dumpling"] = "Half a Dumpling", ["eat_scoop"] = "Spoon", ["chair_cutlery"] = "Fork",
        ["cooking_spoon"] = "Large Wooden Spoon", ["bow_repair"] = "",
        ["old_person_stick"] = "Count Edmont's Walking Stick", ["stick_idle"] = "Count Edmont's Walking Stick",
        ["spray_paint"] = "Spraypaint", ["baby_hold"] = "Baby", ["leaf_fan"] = "Palm Leaf",
        ["paper_bag_listen"] = "Paper bag", ["handbills_call"] = "Paper", ["handbills_look"] = "Paper",
        ["hold_gun"] = "Handgun", ["gun_shoot"] = "Handgun", ["draw_picture"] = "Paint Brush", ["draw_picture2"] = "Paint Brush",
        ["lute_tuning"] = "Lute", ["morin_khuur"] = "Morin Khuur Fiddle",
        ["play_flute"] = "Flute", ["play_vertical_flute"] = "Flute",
        ["test_tube"] = "Test Tube", ["test_tube2"] = "Test Tube", ["beaker"] = "Test Tube",
        ["pinwheel"] = "Paper Pinwheel",
        ["bt_lipstick_gaia"] = "Lipstick", ["bt_lipstick_ryne"] = "Lipstick",
        ["bt_necklace_gaia"] = "Necklace", ["bt_necklace_ryne"] = "Necklace",
        ["guard_sasumata"] = "Pole", ["sasumata_action"] = "Pole", ["dagger_touch"] = "Knife",
        ["chair_cardgame"] = "Card Spread", ["cardgame"] = "Card Spread",
    };

    private static readonly (string[] Words, string Prop)[] AutoPropRules =
    [
        (["carry", "noitem"], ""),
        (["tomestone"], "Tomestone Phone"),
        (["tea"], "Tea Cup"), (["sake"], "Sake Cup"), (["wine"], "Wine Cup"),
        (["beer", "ale", "tankard", "beerhall"], "Tankard of Ale"),
        (["mug"], "Brass Mug"), (["tumbler"], "Glass with Caramel Drink"),
        (["drink", "hotdrink", "drunk", "tumbler", "cup"], "Cup"),
        (["tobacco", "smoke", "smoking", "pipe"], "White Smoking Pipe"),
        (["letter", "note"], "Letter or note"), (["scroll"], "Hingan Scroll"),
        (["book", "read", "reading", "study", "tome"], "Open Book"),
        (["write", "writing", "memo", "quill", "pen"], "Feather Quill"),
        (["sweep", "broom"], "Broom"),
        (["fan"], "Japanese Fan"), (["umbrella"], "Red Japanese Umbrella"), (["lantern"], "Lantern"),
        (["flower", "bouquet"], "Bouquet of Flowers"), (["basket"], "Basket"), (["bottle"], "Wine Bottle"),
        (["bread"], "Bread Bun"), (["tacos", "taco"], "Taco"),
        (["harp", "bowharp"], "Harp"),
        // tools: a pose whose motion clearly works a tool shouldn't do it empty-handed (bike maintenance)
        (["maintenance", "maintain", "repair", "fix", "mechanic", "wrench", "spanner"], "Wrench"),
        (["sledgehammer"], "Big Hammer"), (["hammer", "hammering", "smith", "anvil"], "Hammer"),
        (["paint", "painting"], "Paint Brush"), (["rake"], "Rake"), (["pitchfork"], "Pitchfork"),
        (["ladle", "stir", "cook", "cooking"], "Cooking Ladle"), (["oar", "rowing"], "Oar"),
        (["wipe", "scrub"], "Rag"), (["snow", "scoop", "diggy", "dig", "shovel"], "Shovel"),
        (["reaping", "mowing", "sickle"], "Sickle"), (["grass"], "Sickle"),
        (["axe", "woodchop"], "Axe"), (["katana"], "Katana"), (["harpoon"], "Spearfishing Gig"),
        (["brush"], "Hair Brush"), (["waiter"], "Tray with Glasses"),
    ];

    /// <summary>b26: a pose meant to be performed seated ON something (chair, bench, stool, sofa…), as opposed to
    /// on the ground. Used to snap the DM into a chair with a native /sit first.</summary>
    public static bool IsChairPose(LibEntry e)
    {
        var leaf = e.Key[(e.Key.IndexOf('/') + 1)..];
        var words = Regex.Split(leaf, @"[_\d]+");
        return words.Any(w => w is "chair" or "seat" or "bench" or "stool" or "sofa" or "couch" or "throne")
               && !words.Any(w => w is "ground" or "floor" or "agura");
    }

    // "Name" | "Name|off" | "wSET bBASE vVAR=Label" (a model not in props.csv) | either with "|off". "" = empty hand.
    private static readonly Regex ModelSpec = new(@"^w(\d+) b(\d+) v(\d+)=(.+)$");
    private HandProp? ParseOverride(string spec)
    {
        if (spec.Length == 0) return null;
        var off = spec.EndsWith("|off", StringComparison.Ordinal);
        if (off) spec = spec[..^4];
        var m = ModelSpec.Match(spec);
        var p = m.Success
            ? new HandProp(m.Groups[4].Value, ushort.Parse(m.Groups[1].Value), ushort.Parse(m.Groups[2].Value), ushort.Parse(m.Groups[3].Value))
            : _propByName.GetValueOrDefault(spec);
        return p == null ? null : off ? p with { OffHand = true } : p;
    }

    /// <summary>The prop stem of a timeline key: the leaf without event_/event_base_ and _start/_loop.</summary>
    private static string StemOf(string key)
    {
        var leaf = key[(key.IndexOf('/') + 1)..];
        var m = Suffix.Match(leaf);
        return Regex.Replace(m.Success ? leaf[..m.Index] : leaf, @"^event_(base_)?", "");
    }

    // ---- b57: the game's own pose -> prop pairing -----------------------------------------------------------
    // DM: "always do things that do not require manual maintenance and use the game's own stuff". Every world NPC row
    // (ENpcBase) names the pose it stands in (EventHandler, an ActionTimeline id) and what it holds (ModelMainHand /
    // ModelOffHand, falling back to its NpcEquip row for an empty slot). Tallied per pose stem at load, the most common
    // held PROP model (the 9000-range w90xx sets; weapons on armed guards are ignored) is the pose's prop, in whichever
    // hand holds it more often (main on a tie). It stays right after every patch with nothing to ship. The hand-made
    // PropOverrides and word rules now only cover poses no NPC stands in.
    private Dictionary<string, HandProp> _npcProps = new(StringComparer.Ordinal);

    private Dictionary<string, HandProp> BuildNpcProps(IDataManager data, IPluginLog log)
    {
        var result = new Dictionary<string, HandProp>(StringComparer.Ordinal);
        try
        {
            var nameByModel = new Dictionary<(ushort, ushort, ushort), string>();
            foreach (var p in Props) nameByModel.TryAdd((p.Set, p.Base, p.Variant), p.Name);

            // stem -> (npc count, model -> (main count, off count))
            var tally = new Dictionary<string, (int Npcs, Dictionary<(ushort, ushort, ushort), (int Main, int Off)> Models)>(StringComparer.Ordinal);
            var rows = 0;
            foreach (var row in data.GetExcelSheet<ENpcBase>())
            {
                var tid = (uint)row.EventHandler;
                if (tid == 0 || tid > ushort.MaxValue || !_keyById.TryGetValue((ushort)tid, out var key)) continue;
                ulong main = row.ModelMainHand, off = row.ModelOffHand;
                if ((main == 0 || off == 0) && row.NpcEquip.RowId != 0 && row.NpcEquip.ValueNullable is { } eq)
                {
                    if (main == 0) main = eq.ModelMainHand;
                    if (off == 0) off = eq.ModelOffHand;
                }
                var stem = StemOf(key);
                if (!tally.TryGetValue(stem, out var t)) t = (0, new());
                t.Npcs++;
                Count(t.Models, main, offHand: false);
                Count(t.Models, off, offHand: true);
                tally[stem] = t;
                rows++;
            }

            foreach (var (stem, t) in tally)
            {
                if (t.Models.Count == 0) continue;
                var best = t.Models.OrderByDescending(kv => kv.Value.Main + kv.Value.Off).First();
                var holders = best.Value.Main + best.Value.Off;
                // Noise floor: at least 2 NPCs and a fifth of the pose's NPCs hold it (a lone pose NPC counts).
                if (t.Npcs > 1 && (holders < 2 || holders * 5 < t.Npcs)) continue;
                var (set, bas, variant) = best.Key;
                var name = nameByModel.GetValueOrDefault(best.Key) ?? $"w{set} b{bas} v{variant}";
                result[stem] = new HandProp(name, set, bas, variant, OffHand: best.Value.Off > best.Value.Main);
            }
            log.Information($"PoseLibrary: {result.Count} pose props from {rows} world NPCs ({tally.Count} poses NPCs stand in).");
            foreach (var check in new[] { "stand_read", "stand_with_book", "chair_read", "chair_read_eat" })
                if (tally.TryGetValue(check, out var ct))
                    log.Information($"PoseLibrary: check {check}: {ct.Npcs} NPCs; " +
                                    string.Join(", ", ct.Models.OrderByDescending(kv => kv.Value.Main + kv.Value.Off).Take(3)
                                        .Select(kv => $"w{kv.Key.Item1} b{kv.Key.Item2} v{kv.Key.Item3} main {kv.Value.Main} off {kv.Value.Off}")));
        }
        catch (Exception e) { log.Warning($"PoseLibrary: NPC pose props unavailable ({e.Message}); using the word rules."); }
        return result;

        static void Count(Dictionary<(ushort, ushort, ushort), (int Main, int Off)> models, ulong v, bool offHand)
        {
            var set = (ushort)(v & 0xFFFF);
            if (set < 9000 || set == 0xFFFF) return;   // empty, sentinel, or a real weapon (props are the w90xx sets)
            var k = (set, (ushort)((v >> 16) & 0xFFFF), (ushort)((v >> 32) & 0xFFFF));
            var c = models.GetValueOrDefault(k);
            models[k] = offHand ? (c.Main, c.Off + 1) : (c.Main + 1, c.Off);
        }
    }

    private IReadOnlyList<LibGroup> AttachAutoProps(IReadOnlyList<LibGroup> groups)
    {
        if (_propByName.Count == 0 && _npcProps.Count == 0) return groups;
        var differ = new HashSet<string>(StringComparer.Ordinal);
        HandProp? For(string key)
        {
            var stem = StemOf(key);
            if (_npcProps.TryGetValue(stem, out var npc))
            {
                if (PropOverrides.TryGetValue(stem, out var hand) && ParseOverride(hand) is { } h
                    && (h.Set, h.Base, h.Variant, h.OffHand) != (npc.Set, npc.Base, npc.Variant, npc.OffHand)
                    && differ.Add(stem))
                    _log?.Information($"PoseLibrary: {stem}: the NPCs hold {npc.Name} (w{npc.Set} b{npc.Base} v{npc.Variant}{(npc.OffHand ? " off" : "")}); the old hand-made pick was {h.Name}{(h.OffHand ? " off" : "")}.");
                return npc;
            }
            if (PropOverrides.TryGetValue(stem, out var named)) return ParseOverride(named);
            var words = Regex.Split(stem, @"[_\d]+").Where(w => w.Length > 0).ToHashSet(StringComparer.Ordinal);
            foreach (var (ws, prop) in AutoPropRules)
                if (ws.Any(words.Contains))
                    return prop.Length > 0 && _propByName.TryGetValue(prop, out var p) ? p : null;
            return null;
        }
        var outGroups = groups.Select(g => g with { Items = g.Items.Select(e => For(e.Key) is { } p ? e with { Prop = p } : e).ToList() }).ToList();
        if (differ.Count > 0) _log?.Information($"PoseLibrary: {differ.Count} pose props now follow the NPCs instead of the old hand-made pick.");
        return outGroups;
    }
}
