using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Penumbra.Api.Enums;
using Penumbra.Api.IpcSubscribers;
using GameObjectStruct = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using ObjectKindStruct = FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectKind;

namespace HDM;

/// <summary>
/// b48 (F-02, step 1): the DM's Penumbra mods on the DM's own puppets, FROZEN per puppet.
///
/// 1. On spawn the puppet is given the DM's own collection (an individual assignment), so its first draw resolves the
///    DM's current mods (b47).
/// 2. ~2 s later, once it has loaded, the files it actually used are read back (GetGameObjectResourcePaths): every file
///    served from disk becomes a fixed redirect (game path -> that file), plus the meta manipulations it uses.
/// 3. Those go into a PRIVATE temporary collection (one temporary mod), the puppet is moved onto it, and the individual
///    assignment is removed. From then on the DM's mod toggles and any redraw leave the puppet exactly as summoned - which
///    is how the DM combines mods: toggle, summon, toggle, summon.
/// Despawn, switching the option off, and unloading HDM delete the temporary collection and clear any assignment.
///
/// The frozen redirects are also the sync payload's raw material (step 2): each is "game path -> mod folder + path inside
/// it", which a peer can rebuild from their own copy of the mod. Nothing is copied or transferred here; the files stay
/// in the DM's mod folders.
///
/// Penumbra identifies actors strictly in its assignment / meta calls (FromObject allowPlayerNpc: false), and a puppet is a
/// NameId-0 BattleNpc that only identifies leniently, as Player(name, home world) - the identity its renderer uses. So
/// those calls run with the puppet briefly presented as a player character (<see cref="AsPlayer{T}"/>).
/// Only files loaded by snapshot time are frozen: a modded animation or effect a puppet loads later plays vanilla.
/// </summary>
public sealed unsafe class PuppetMods : IDisposable
{
    private const string Identity = "HDM";
    private const string FreezeTag = "HDM.Frozen";

    private readonly IFramework _framework;
    private readonly IObjectTable _objects;
    private readonly IPluginLog _log;
    private readonly GetCollectionForObject _getCollection;
    private readonly SetCollectionForObject _setCollection;
    private readonly GetGameObjectResourcePaths _resourcePaths;
    private readonly GetMetaManipulations _meta;
    private readonly GetModDirectory _modDir;
    private readonly CreateTemporaryCollection _createTemp;
    private readonly DeleteTemporaryCollection _deleteTemp;
    private readonly AssignTemporaryCollection _assignTemp;
    private readonly AddTemporaryMod _addTempMod;
    private readonly RedrawObject _redraw;

    private sealed class Pending { public long Due; public int Tries; public bool TopUp; }
    private readonly Dictionary<int, Pending> _pending = new();

    /// <summary>A frozen puppet: its temporary collection and the redirects it carries (game path -> file on disk).</summary>
    public sealed record Frozen(Guid Collection, IReadOnlyDictionary<string, string> Redirects, string Meta);
    private readonly Dictionary<int, Frozen> _frozen = new();

    public PuppetMods(IDalamudPluginInterface pi, IFramework framework, IObjectTable objects, IPluginLog log)
    {
        _framework = framework; _objects = objects; _log = log;
        _getCollection = new GetCollectionForObject(pi);
        _setCollection = new SetCollectionForObject(pi);
        _resourcePaths = new GetGameObjectResourcePaths(pi);
        _meta = new GetMetaManipulations(pi);
        _modDir = new GetModDirectory(pi);
        _createTemp = new CreateTemporaryCollection(pi);
        _deleteTemp = new DeleteTemporaryCollection(pi);
        _assignTemp = new AssignTemporaryCollection(pi);
        _addTempMod = new AddTemporaryMod(pi);
        _redraw = new RedrawObject(pi);
        _framework.Update += Tick;
    }

    public IReadOnlyDictionary<int, Frozen> FrozenPuppets => _frozen;

    /// <summary>b49: raised after one of the DM's own puppets is frozen (packed mod list) or released (null), for sync.
    /// Not raised for peers' mirrors (those come IN through <see cref="ApplyRemote"/>).</summary>
    public Action<int, string?>? FrozenForSync { get; set; }
    private readonly HashSet<int> _remote = new();   // mirrors whose collection came from a peer

    // The wire form of a frozen puppet: per mod folder, [game path, path inside the mod] pairs, plus small meta.
    private sealed class Wire
    {
        public Dictionary<string, List<string[]>> M { get; set; } = new();
        public List<string[]>? S { get; set; }   // b53: swaps, [game path, target game path] - the same on every client
        public string? Meta { get; set; }
    }
    private const int MaxMetaChars = 1500;  // b50: the bike's meta was 932 and got dropped at 600; the atom has room (cap 2048)

    /// <summary>A puppet the DM just spawned (before its first draw): give it the DM's collection and schedule the freeze.</summary>
    public void Begin(int idx)
    {
        End(idx, redraw: false, announce: false);   // a recycled index / repeated name must not inherit an old collection; the re-freeze announces
        if (_objects.LocalPlayer is not { } me) return;
        try
        {
            var (valid, _, mine) = _getCollection.Invoke(me.ObjectIndex);
            if (!valid) { _log.Information($"PuppetMods: no collection for you; puppet obj#{idx} stays stock."); return; }
            var (ec, _) = AsPlayer(idx, () => _setCollection.Invoke(idx, mine.Id, allowCreateNew: true, allowDelete: true));
            _log.Information($"PuppetMods: puppet obj#{idx} draws with your collection {mine.Name} -> {ec}");
            _pending[idx] = new Pending { Due = Environment.TickCount64 + 2000 };
        }
        catch (Exception e) { _log.Warning($"PuppetMods: Penumbra unavailable ({e.GetType().Name})."); }
    }

    /// <summary>Undo everything for a puppet: delete its frozen collection, clear any individual assignment, optionally redraw.</summary>
    /// <remarks>b51: <paramref name="announce"/> = tell peers the mods are gone. False when the puppet is about to be
    /// despawned or re-frozen: a "mods: none" update that overtakes the despawn on the wire made HMS on the peer
    /// resurrect the puppet as an orphan copy (DM's two-client test).</remarks>
    public void End(int idx, bool redraw, bool announce = true)
    {
        _pending.Remove(idx);
        var wasRemote = _remote.Remove(idx);
        try
        {
            if (_frozen.Remove(idx, out var f))
            {
                _deleteTemp.Invoke(f.Collection);
                if (!wasRemote && announce) FrozenForSync?.Invoke(idx, null);
            }
            if (_objects[idx] is { Address: not 0 })
            {
                AsPlayer(idx, () => _setCollection.Invoke(idx, null, allowCreateNew: false, allowDelete: true));
                if (redraw) _redraw.Invoke(idx, RedrawType.Redraw);
            }
        }
        catch { /* Penumbra gone */ }
    }

    private void Tick(IFramework _)
    {
        if (_pending.Count == 0) return;
        var now = Environment.TickCount64;
        foreach (var (idx, p) in new List<KeyValuePair<int, Pending>>(_pending))
        {
            if (now < p.Due) continue;
            if (_objects[idx] is not { Address: not 0 }) { _pending.Remove(idx); continue; }
            if (p.TopUp) { _pending.Remove(idx); TopUp(idx); continue; }
            if (TryFreeze(idx)) { if (_pending.TryGetValue(idx, out var q) && q == p) _pending.Remove(idx); continue; }
            if (++p.Tries >= 5) { _pending.Remove(idx); _log.Information($"PuppetMods: puppet obj#{idx} had no resources to freeze; it keeps your live collection."); }
            else p.Due = now + 1500;
        }
    }

    private bool TryFreeze(int idx)
    {
        try
        {
            var trees = _resourcePaths.Invoke((ushort)idx);
            if (trees.Length == 0 || trees[0] is not { Count: > 0 } paths) return false;
            var redirects = Collect(paths);
            var meta = AsPlayer(idx, () => _meta.Invoke(idx)) ?? string.Empty;

            var ec = _createTemp.Invoke(Identity, $"HDM puppet obj#{idx}", out var coll);
            if (ec != PenumbraApiEc.Success) { _log.Warning($"PuppetMods: temporary collection refused ({ec})."); return true; }
            var mod = _addTempMod.Invoke(FreezeTag, coll, redirects, meta, 0);
            var assign = AsPlayer(idx, () => _assignTemp.Invoke(coll, idx, true));
            AsPlayer(idx, () => _setCollection.Invoke(idx, null, allowCreateNew: false, allowDelete: true));
            _frozen[idx] = new Frozen(coll, redirects, meta);
            FrozenForSync?.Invoke(idx, PackForSync(redirects, meta));
            _log.Information($"PuppetMods: puppet obj#{idx} frozen: {redirects.Count} modded files ({Swaps(redirects)} swaps) from {ModsIn(redirects)}, " +
                             $"meta {meta.Length} chars (temp mod {mod}, assign {assign})");
            // b53: a big model can still be streaming textures/materials at 2 s; look again at ~6 s and add what's new.
            _pending[idx] = new Pending { Due = Environment.TickCount64 + 4000, TopUp = true };
            return true;
        }
        catch (Exception e) { _log.Warning($"PuppetMods: freeze failed for obj#{idx}: {e.Message}"); return true; }
    }

    /// <summary>b53: the puppet's modded resources: files served from disk AND Penumbra swaps (a game path served by
    /// ANOTHER game path - a mod that reuses an existing material/texture instead of shipping one). b48-b52 kept files only,
    /// so a swap-based mod (the clawed crane's metal/onyx) froze as "1 modded file" and rendered with stock materials.</summary>
    private static Dictionary<string, string> Collect(Dictionary<string, HashSet<string>> paths)
    {
        var redirects = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (actual, gamePaths) in paths)
        {
            var onDisk = Path.IsPathRooted(actual);
            if (onDisk && !File.Exists(actual)) continue;
            foreach (var gp in gamePaths)
                if (onDisk || !string.Equals(gp, actual, StringComparison.OrdinalIgnoreCase))
                    redirects[gp] = actual;   // a file, or a swap to another game path
        }
        return redirects;
    }

    private static int Swaps(IReadOnlyDictionary<string, string> r) => r.Values.Count(v => !Path.IsPathRooted(v));

    /// <summary>b53: second look ~6 s after the spawn; anything newly loaded joins the frozen mod and peers get the update.</summary>
    private void TopUp(int idx)
    {
        if (!_frozen.TryGetValue(idx, out var f) || _remote.Contains(idx)) return;
        try
        {
            var trees = _resourcePaths.Invoke((ushort)idx);
            if (trees.Length == 0 || trees[0] is not { Count: > 0 } paths) return;
            var merged = new Dictionary<string, string>(f.Redirects, StringComparer.Ordinal);
            var added = 0;
            foreach (var (gp, target) in Collect(paths))
                if (merged.TryAdd(gp, target)) added++;
            if (added == 0) return;
            _addTempMod.Invoke(FreezeTag, f.Collection, merged, f.Meta, 0);
            _frozen[idx] = f with { Redirects = merged };
            FrozenForSync?.Invoke(idx, PackForSync(merged, f.Meta));
            _log.Information($"PuppetMods: puppet obj#{idx} top-up: +{added} late-loaded resources ({merged.Count} total)");
        }
        catch (Exception e) { _log.Warning($"PuppetMods: top-up failed for obj#{idx}: {e.Message}"); }
    }

    // "Garlean Tent, Some Other Mod" - the mod folders the frozen files come from (for the log; the sync step uses it too).
    private string ModsIn(Dictionary<string, string> redirects)
    {
        string root;
        try { root = _modDir.Invoke(); } catch { return "?"; }
        var mods = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in redirects.Values)
            if (file.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                var rel = file[root.Length..].TrimStart('\\', '/');
                var cut = rel.IndexOfAny(['\\', '/']);
                mods.Add(cut > 0 ? rel[..cut] : rel);
            }
        return mods.Count == 0 ? "(no mods: stock)" : string.Join(", ", mods);
    }

    /// <summary>b49: the frozen redirects as "mod folder + path inside the mod" per game path, packed for the sync atom.
    /// Files outside the Penumbra mod folder (another tool's cache) can't be rebuilt on a peer and are left out.</summary>
    private string? PackForSync(IReadOnlyDictionary<string, string> redirects, string meta)
    {
        string root;
        try { root = _modDir.Invoke(); } catch { return null; }
        var wire = new Wire { Meta = meta.Length is > 0 and <= MaxMetaChars ? meta : null };
        foreach (var (gamePath, file) in redirects)
        {
            if (!Path.IsPathRooted(file)) { (wire.S ??= new()).Add([gamePath, file]); continue; }   // b53: a swap
            if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            var rel = file[root.Length..].TrimStart('\\', '/');
            var cut = rel.IndexOfAny(['\\', '/']);
            if (cut <= 0) continue;
            var mod = rel[..cut];
            if (!wire.M.TryGetValue(mod, out var list)) wire.M[mod] = list = new List<string[]>();
            list.Add([gamePath, rel[(cut + 1)..].Replace('\\', '/')]);
        }
        return wire.M.Count == 0 && wire.S == null && wire.Meta == null ? null : LookCodec.Pack(JsonConvert.SerializeObject(wire));
    }

    /// <summary>b49: a PEER's puppet (its mirror on this client) carries mods: rebuild the redirects from THIS client's own
    /// copy of those mod folders, give the mirror a private temporary collection with them, and redraw it. Mod folders not
    /// installed here are listed in the log (the files come from the DM via a Mare-style tool, or by sharing the mod).
    /// Null = the peer released them: back to stock. b50: no redraw of its own - the CALLER redraws through HDM's guise
    /// redraw (a separate Penumbra redraw collided with the guise apply in flight: stock or invisible copies).</summary>
    public void ApplyRemote(int idx, string? packed)
    {
        End(idx, redraw: false);
        if (packed == null || LookCodec.Unpack(packed) is not { } json) return;
        try
        {
            var wire = JsonConvert.DeserializeObject<Wire>(json);
            if (wire == null) return;
            var root = _modDir.Invoke();
            var redirects = new Dictionary<string, string>(StringComparer.Ordinal);
            var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (mod, pairs) in wire.M)
                foreach (var pair in pairs)
                {
                    if (pair.Length < 2) continue;
                    var file = Path.Combine(root, mod, pair[1].Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(file)) redirects[pair[0]] = file;
                    else missing.Add(mod);
                }
            if (wire.S != null)
                foreach (var sw in wire.S)
                    if (sw.Length >= 2) redirects[sw[0]] = sw[1];   // b53: swaps need no local files
            if (missing.Count > 0)
                _log.Information($"PuppetMods: peer puppet obj#{idx} uses mods not installed here (partly or fully): {string.Join(", ", missing)}");
            if (redirects.Count == 0 && wire.Meta == null) return;

            var ec = _createTemp.Invoke(Identity, $"HDM peer puppet obj#{idx}", out var coll);
            if (ec != PenumbraApiEc.Success) { _log.Warning($"PuppetMods: temporary collection refused ({ec})."); return; }
            _addTempMod.Invoke(FreezeTag, coll, redirects, wire.Meta ?? string.Empty, 0);
            var assign = AsPlayer(idx, () => _assignTemp.Invoke(coll, idx, true));
            _frozen[idx] = new Frozen(coll, redirects, wire.Meta ?? string.Empty);
            _remote.Add(idx);
            _log.Information($"PuppetMods: peer puppet obj#{idx} wears {redirects.Count} modded files from {string.Join(", ", wire.M.Keys)} (assign {assign})");
        }
        catch (Exception e) { _log.Warning($"PuppetMods: peer mods for obj#{idx} failed: {e.Message}"); }
    }

    /// <summary>Run one Penumbra call with the puppet presented as a player character (see class remarks), then restore.</summary>
    private T AsPlayer<T>(int idx, Func<T> call)
    {
        if (_objects[idx] is not { Address: not 0 } obj) return call();
        var go = (GameObjectStruct*)obj.Address;
        var kind = go->ObjectKind;
        go->ObjectKind = ObjectKindStruct.Pc;
        try { return call(); }
        finally { go->ObjectKind = kind; }
    }

    public void Dispose()
    {
        _framework.Update -= Tick;
        foreach (var idx in new List<int>(_frozen.Keys)) End(idx, redraw: false);
        _pending.Clear();
    }
}
