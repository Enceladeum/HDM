using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace HDM;

/// <summary>
/// Tier A3 location source: the DM's OWN runtime observations — "be your own gubal". Passively
/// samples the object table while playing INSTANCED content and records every live BattleNpc's
/// <c>BaseId → TerritoryType</c> (plus its live localized name) to a user-local CSV. This is the
/// only source that reaches the instanced roster: combat mobs are server-spawned, so no static
/// client table (Level Type 9, LGB <c>instances.csv</c>) places a dungeon/trial/raid mob, and
/// Teamcraft's crowd data is 100% overworld (0 dungeon maps). The white androids and ground-VFX
/// bases of the YoRHa raids — never named, in no offline table — get a home the instant the DM
/// walks the duty. See <c>docs/runtime-mob-territory-harvester-spec.md</c> for the grounding.
///
/// WHY THIS SHAPE. "If it has an id and spawns, it has a fingerprint": a live BattleNpc exposes
/// its BNpcBase id (<see cref="IGameObject.BaseId"/> — the same key the catalog joins on), its
/// localized <c>Name</c>, and its <c>NameId</c> (BNpcName), while <see cref="IClientState.TerritoryType"/>
/// is always known. Logging the pair recovers BOTH the instanced territory table AND the unnamed
/// catalog's live names, is patch-proof (a new patch's mobs self-populate the first time anyone
/// walks the content), and needs no crowdsource round-trip.
///
/// SAFETY (spec §7). Framework-thread reads only, throttled to one pass per ~2 s, and ONLY while
/// in a duty (<see cref="ContentInfo.IsDuty"/>) so the open world — already covered by the shipped
/// CSV — costs nothing. No hooks, no packets, no native writes, no <c>unsafe</c>: it cannot CTD.
/// Persists to the plugin CONFIG dir (survives plugin updates, unlike <c>Data/</c>); the on-disk
/// row shape matches the shipped <c>mob-territory-index.csv</c> so a future offline pipeline can
/// fold it straight back into the offline dataset, closing the runtime→file loop.
///
/// PRIORITY. Consulted just BELOW the curated instanced roster (BossMod-derived) and ABOVE the
/// name/sheet/web tiers: a first-hand sighting outranks a wiki scrape but yields to a hand-verified
/// roster where one exists. For the YoRHa raids the curated roster is essentially empty, so this
/// tier is what places them.
/// </summary>
public sealed class MobHarvester : IDisposable
{
    // One observation bucket per (BaseId, TerritoryType) sighting.
    private sealed class Obs
    {
        public int Count;
        public int MaxConcurrent;      // most simultaneous copies in one pass (boss=1, adds/trash=pack)
        public string LastName = "";
        public uint NameId;            // BNpcName id (0 = none)
        public DateTime FirstSeenUtc;
        public DateTime LastSeenUtc;
    }

    private readonly IFramework _framework;
    private readonly IObjectTable _objects;
    private readonly IClientState _clientState;
    private readonly ContentIndex _content;
    private readonly IPluginLog _log;
    private readonly string _path;

    private readonly Dictionary<(uint baseId, uint terr), Obs> _obs = new();
    // Fast query maps, kept in sync with _obs: base -> its most-observed home, and base -> a live name.
    private readonly Dictionary<uint, (uint terr, int weight)> _byBase = new();
    private readonly Dictionary<uint, (string name, uint nameId)> _nameByBase = new();

    private long _nextSampleTick;
    private bool _dirty;               // in-memory changed since last flush (drives territory/logout/dispose flush)
    private const long SampleIntervalMs = 2000;

    /// <summary>Master gate for NEW collection (the Config "update monster names from game spawns" toggle).
    /// When false the sampler is fully inert — no object-table scan, no disk writes — but rows already loaded
    /// from disk still answer <see cref="TryGetPrimary"/>/<see cref="TryGetName"/>, so the catalog keeps using
    /// past sightings. Seeded from <see cref="Configuration.HarvestMobNames"/> at startup and written when the
    /// toggle flips. Default false: a fresh install harvests nothing until the DM opts in.</summary>
    public bool Enabled;

    /// <summary>Distinct BNpcBases with at least one runtime sighting (startup log / diagnostics).</summary>
    public int Count => _byBase.Count;

    /// <summary>Distinct BNpcBases for which a live localized name has been captured. Grows only when a
    /// brand-new base is first named at runtime, so MainWindow watches it as a cheap change-signal: when
    /// it ticks up, re-push the harvested names into the catalog rows' <see cref="MobRow.LiveName"/>.</summary>
    public int NameCount => _nameByBase.Count;

    public MobHarvester(IFramework framework, IObjectTable objects, IClientState clientState,
                        ContentIndex content, IDalamudPluginInterface pi, IPluginLog log)
    {
        _framework = framework;
        _objects = objects;
        _clientState = clientState;
        _content = content;
        _log = log;
        _path = Path.Combine(pi.GetPluginConfigDirectory(), "mob-territory-harvested.csv");

        Load();

        _framework.Update += OnUpdate;
        _clientState.TerritoryChanged += OnTerritoryChanged;
        _clientState.Logout += OnLogout;
    }

    /// <summary>Resolve a BNpcBase to the instanced territory it was most-often observed in. False
    /// for any base never seen at runtime (the overworld tail — covered by the shipped CSV — and
    /// content the DM hasn't walked yet).</summary>
    public bool TryGetPrimary(uint baseId, out uint territoryId)
    {
        if (_byBase.TryGetValue(baseId, out var v)) { territoryId = v.terr; return true; }
        territoryId = 0;
        return false;
    }

    /// <summary>Resolve a BNpcBase to a live localized name seen at runtime — the name-recovery
    /// signal for the ~500 catalog-blank bases (e.g. the YoRHa androids). False if never named.</summary>
    public bool TryGetName(uint baseId, out string name)
    {
        if (_nameByBase.TryGetValue(baseId, out var v) && v.name.Length > 0) { name = v.name; return true; }
        name = "";
        return false;
    }

    // --- Sampling -------------------------------------------------------------

    private void OnUpdate(IFramework _)
    {
        if (!Enabled) return; // Config gate: no sampling, no scan, no disk writes until the DM opts in.
        var now = Environment.TickCount64;
        if (now < _nextSampleTick) return;
        _nextSampleTick = now + SampleIntervalMs;
        try { Sample(); }
        catch (Exception e) { _log.Error(e, "MobHarvester: sample pass failed."); }
    }

    private void Sample()
    {
        uint territory = _clientState.TerritoryType;
        if (territory == 0) return;
        // Duty-only: the open world already ships as a CSV; instanced rosters are the gap.
        if (!_content.TryGet(territory, out var ci) || !ci.IsDuty) return;

        // One pass = one snapshot. Tally per-base concurrency here (for MaxConcurrent) and carry the
        // name/NameId of the first copy seen, so we upsert each base exactly once per pass.
        var pass = new Dictionary<uint, (int conc, string name, uint nameId)>();
        foreach (var obj in _objects)
        {
            if (obj is null || obj.ObjectKind != ObjectKind.BattleNpc) continue;
            if (obj is not IBattleNpc bnpc) continue;
            if (bnpc.BattleNpcKind != BattleNpcSubKind.Combatant) continue; // enemies/guards only — drop pets, chocobos, buddies, trust allies
            var baseId = bnpc.BaseId;
            if (baseId == 0) continue;
            var name = bnpc.Name.TextValue ?? "";
            var nameId = bnpc.NameId;
            if (pass.TryGetValue(baseId, out var p))
                pass[baseId] = (p.conc + 1, p.name.Length > 0 ? p.name : name, p.nameId != 0 ? p.nameId : nameId);
            else
                pass[baseId] = (1, name, nameId);
        }

        var utc = DateTime.UtcNow;
        var newPairs = 0;
        foreach (var (baseId, p) in pass)
            if (Upsert(baseId, territory, p.conc, p.name, p.nameId, utc)) newPairs++;

        // Change-gated: only touch disk when a brand-new (base, territory) pair first appears.
        if (newPairs > 0) Flush();
    }

    // Returns true if this created a NEW (base, territory) pair.
    private bool Upsert(uint baseId, uint territory, int concurrent, string name, uint nameId, DateTime utc)
    {
        var key = (baseId, territory);
        var isNew = !_obs.TryGetValue(key, out var o);
        if (isNew) { o = new Obs { FirstSeenUtc = utc }; _obs[key] = o; }

        o!.Count++;
        o.LastSeenUtc = utc;
        if (concurrent > o.MaxConcurrent) o.MaxConcurrent = concurrent;
        if (name.Length > 0) o.LastName = name;
        if (nameId != 0) o.NameId = nameId;
        _dirty = true;

        // Primary home = the territory with the most sightings for this base.
        if (!_byBase.TryGetValue(baseId, out var best) || o.Count > best.weight)
            _byBase[baseId] = (territory, o.Count);
        // First live name wins (a base's name is stable; avoid churn).
        if (name.Length > 0 && !_nameByBase.ContainsKey(baseId))
            _nameByBase[baseId] = (name, nameId);

        return isNew;
    }

    // --- Persistence (config dir, whole-file rewrite; the file stays small) ----

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            using var r = new StreamReader(_path, Encoding.UTF8);
            r.ReadLine(); // header
            string? line;
            while ((line = r.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                var f = line.Split(',');
                if (f.Length < 3) continue;
                if (!uint.TryParse(f[0], out var baseId)) continue;
                if (!uint.TryParse(f[1], out var terr)) continue;
                int.TryParse(f[2], out var count);
                var maxc  = f.Length > 3 && int.TryParse(f[3], out var mc) ? mc : 0;
                var nameId = f.Length > 4 && uint.TryParse(f[4], out var ni) ? ni : 0u;
                var name  = f.Length > 5 ? f[5] : "";
                DateTime.TryParse(f.Length > 6 ? f[6] : "", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var first);
                DateTime.TryParse(f.Length > 7 ? f[7] : "", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var last);

                var o = new Obs { Count = Math.Max(1, count), MaxConcurrent = maxc, LastName = name, NameId = nameId, FirstSeenUtc = first, LastSeenUtc = last };
                _obs[(baseId, terr)] = o;
                if (!_byBase.TryGetValue(baseId, out var best) || o.Count > best.weight)
                    _byBase[baseId] = (terr, o.Count);
                if (name.Length > 0 && !_nameByBase.ContainsKey(baseId))
                    _nameByBase[baseId] = (name, nameId);
            }
            _log.Information($"MobHarvester: loaded {_obs.Count} harvested (base,territory) rows covering {_byBase.Count} bases from {_path}");
        }
        catch (Exception e)
        {
            _log.Error(e, $"MobHarvester: failed to load {_path}");
        }
    }

    private void Flush()
    {
        try
        {
            var sb = new StringBuilder(_obs.Count * 56 + 96);
            sb.Append("BaseId,TerritoryTypeId,Observations,MaxConcurrent,NameId,Name,FirstSeenUtc,LastSeenUtc\n");
            foreach (var kv in _obs)
            {
                var o = kv.Value;
                sb.Append(kv.Key.baseId).Append(',')
                  .Append(kv.Key.terr).Append(',')
                  .Append(o.Count).Append(',')
                  .Append(o.MaxConcurrent).Append(',')
                  .Append(o.NameId).Append(',')
                  .Append(Sanitize(o.LastName)).Append(',')
                  .Append(o.FirstSeenUtc.ToString("o", CultureInfo.InvariantCulture)).Append(',')
                  .Append(o.LastSeenUtc.ToString("o", CultureInfo.InvariantCulture)).Append('\n');
            }
            File.WriteAllText(_path, sb.ToString(), new UTF8Encoding(false));
            _dirty = false;
        }
        catch (Exception e)
        {
            _log.Error(e, $"MobHarvester: failed to write {_path}");
        }
    }

    // Names carry no commas in practice, but a bare-split CSV can't tolerate one — scrub defensively.
    private static string Sanitize(string s)
        => s.IndexOf(',') >= 0 || s.IndexOf('\n') >= 0 || s.IndexOf('\r') >= 0
            ? s.Replace(',', ' ').Replace('\n', ' ').Replace('\r', ' ')
            : s;

    // Object table is rebuilt across a zone change; flush what we have, then let the next Update
    // pass (forced immediately) sample the new roster once it populates.
    private void OnTerritoryChanged(uint _)
    {
        if (_dirty) Flush();
        _nextSampleTick = 0; // sample the incoming zone on the very next frame
    }

    private void OnLogout(int type, int code)
    {
        if (_dirty) Flush();
    }

    public void Dispose()
    {
        _framework.Update -= OnUpdate;
        _clientState.TerritoryChanged -= OnTerritoryChanged;
        _clientState.Logout -= OnLogout;
        if (_dirty) Flush();
    }
}
