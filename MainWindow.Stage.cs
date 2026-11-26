using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;

namespace HDM;

// b43: the STAGE tab, replacing the separate Spawn and Animations tabs (DM: "you need to hop between tabs and remember
// to click the right puppet"). One surface for running a scene like a board of chess pieces:
//   1. the CAST strip: You, every puppet, "All puppets", "+ Spawn". Clicking a chip makes it the subject; right-click a
//      puppet for Possess / Bring to me / Face me / Despawn. Spawning selects the new puppet; possessing selects it too.
//   2. the SUBJECT bar: who it is and what it wears, Possess/Release, Look (wear / disguise from favourites + search),
//      Despawn or Revert, and Reset on the right.
//   3. PLACEMENT & PLAYBACK (collapsible): position, facing, scale, elevation, speed / freeze, weapon, stances, raw id.
//   4. the animation LIBRARY (unchanged), always acting on the subject; with "All puppets" it plays on every puppet.
// Subject state reuses the existing fields: _animApplyToPuppet + _selectedPuppet (a puppet), plus _stageAll.
public sealed partial class MainWindow
{
    private bool _stageAll;              // "All puppets" chip: library plays and stance buttons fan out to every puppet
    private bool _stageBroadcasting;     // guard so a fanned-out play doesn't fan out again
    private bool _stagePlacementOpen = true;

    /// <summary>b44: HOutfits' dressing-target IPC (the Stage's "Dress…" button). Set by Plugin.</summary>
    public HOutfitsIpc? Outfits { get; set; }

    private void DrawStageTab()
    {
        var self = Self();
        PrunePuppetLabels();
        DrawCastStrip(self);
        var (target, guise) = StageSubject(self);
        DrawSubjectBar(self, target);
        DrawSubjectControls(self, target);
        DrawLibrary(target, guise);
    }

    /// <summary>The subject the Stage acts on: a puppet, the first puppet when "All puppets" is chosen (its body and
    /// guise scope the library lists), or you.</summary>
    private (ICharacter? Target, MobRow? Guise) StageSubject(ICharacter? self)
    {
        var puppets = _spawn.Spawned;
        if (puppets.Count < 2) _stageAll = false;
        if (_stageAll && _objects[puppets[0]] is ICharacter first)
            return (first, _puppetGuise.GetValueOrDefault(first.ObjectIndex));
        if (_animApplyToPuppet && FocusedPuppet() is { } p)
            return (p, _puppetGuise.GetValueOrDefault(p.ObjectIndex));
        _animApplyToPuppet = false;
        return (self, _wornGuise);
    }

    private void SelectSelf() { _animApplyToPuppet = false; _stageAll = false; }
    private void SelectPuppet(ushort idx) { _selectedPuppet = idx; _animApplyToPuppet = true; _stageAll = false; }

    // ---- 1. cast strip --------------------------------------------------------------------------------------

    /// <summary>The cast chips. b55: also drawn on the Catalog (spawnChip: false; its Spawn button does that).</summary>
    private void DrawCastStrip(ICharacter? self, bool spawnChip = true)
    {
        var puppets = _spawn.Spawned.ToList();
        var right = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X;
        var first = true;

        void Next(string label)
        {
            if (first) { first = false; return; }
            ImGui.SameLine();
            var w = ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2f + 18f;
            if (ImGui.GetCursorScreenPos().X + w > right) ImGui.NewLine();
        }

        // You
        var youLabel = self != null ? $"You · {self.Name.TextValue}" : "You";
        Next(youLabel);
        CastDot(self?.ObjectIndex ?? -1);
        if (Chip(youLabel, "cast_you", !_animApplyToPuppet && !_stageAll)) SelectSelf();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Animate and dress yourself.");

        // Puppets
        foreach (var idx in puppets)
        {
            var label = _puppetGuise.TryGetValue(idx, out var g) ? g.DisplayName : "Blank dummy";
            Next(label);
            CastDot(idx);
            if (Chip(label, $"cast_{idx}", _animApplyToPuppet && !_stageAll && _selectedPuppet == idx)) SelectPuppet(idx);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{CastStatus(idx)} · obj#{idx}\nClick to work on it; right-click for quick actions.");
            if (ImGui.BeginPopupContextItem($"##castctx{idx}"))
            {
                DrawPuppetQuickMenu(idx);
                ImGui.EndPopup();
            }
        }

        // All puppets
        if (puppets.Count >= 2)
        {
            var allLabel = $"All puppets ({puppets.Count})";
            Next(allLabel);
            if (Chip(allLabel, "cast_all", _stageAll)) { _stageAll = true; _animApplyToPuppet = false; }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Animations, stances and Reset apply to every puppet at once.");
        }

        // + Spawn
        if (spawnChip)
        {
            Next("+ Spawn");
            using (new Disabled(self == null))
                if (Chip("+ Spawn", "cast_spawn", false)) { _spawnFilter = ""; ImGui.OpenPopup("##stage_spawn"); }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(self == null ? "Be present in the world to spawn." : "Spawn a puppet from your favourites, a search, or as a blank dummy.");
            ImGui.SetNextWindowSize(new Vector2(440, 420), ImGuiCond.Appearing);
            if (ImGui.BeginPopup("##stage_spawn"))
            {
                DrawLookPicker(spawn: true, self, null);
                ImGui.EndPopup();
            }
        }
        if (_spawnQueue.Count > 0)
        {
            ImGui.TextDisabled($"{_spawnQueue.Count} spawn{(_spawnQueue.Count == 1 ? "" : "s")} queued (one a second while HM-Sync is running)");
            ImGui.SameLine();
            if (ImGui.SmallButton("Cancel##spawnq")) _spawnQueue.Clear();
        }
        ImGui.Spacing();
    }

    // A small status dot before a cast chip: gold = being driven, blue = frozen, green = animating, grey = idle.
    private void CastDot(int idx)
    {
        var col = idx < 0 ? new Vector4(0.45f, 0.47f, 0.52f, 1f)
                : _possession.IsPossessing && _possession.PossessedIndex == idx ? new Vector4(1f, 0.75f, 0.20f, 1f)
                : _anim.IsFrozen(idx) ? new Vector4(0.45f, 0.70f, 1f, 1f)
                : _anim.IsPlaying(idx) ? new Vector4(0.36f, 0.80f, 0.45f, 1f)
                : new Vector4(0.45f, 0.47f, 0.52f, 1f);
        var p = ImGui.GetCursorScreenPos();
        var h = ImGui.GetFrameHeight();
        ImGui.GetWindowDrawList().AddCircleFilled(new Vector2(p.X + 4f, p.Y + h / 2f), 3.5f, ImGui.GetColorU32(col), 12);
        ImGui.Dummy(new Vector2(10f, h));
        ImGui.SameLine(0f, 2f);
    }

    private string CastStatus(int idx)
        => _possession.IsPossessing && _possession.PossessedIndex == idx ? "Driving"
         : _anim.IsFrozen(idx) ? "Frozen"
         : _anim.IsPlaying(idx) ? "Animating"
         : "Idle";

    private void DrawPuppetQuickMenu(ushort idx)
    {
        var possessing = _possession.IsPossessing && _possession.PossessedIndex == idx;
        if (possessing) { if (ImGui.Selectable("Release")) _possession.Release(); }
        else if (_possession.CanPossess(idx) && ImGui.Selectable("Possess")) { SelectPuppet(idx); _possession.Possess(idx); }
        if (ImGui.Selectable("Bring to me")) { _spawn.MoveToLocalPlayer(idx); if (_spawn.TryGetTransform(idx, out var p, out var r)) ReportPuppetMove(idx, p, r); }
        if (ImGui.Selectable("Face me")) { _spawn.FaceLocalPlayer(idx); if (_spawn.TryGetTransform(idx, out var p, out var r)) ReportPuppetMove(idx, p, r); }
        if (ImGui.Selectable("Puppet bar")) { SelectPuppet(idx); _showPuppetBar = true; }
        ImGui.Separator();
        if (ImGui.Selectable("Despawn")) DespawnPuppet(idx);
    }

    private void DespawnPuppet(ushort idx)
    {
        SetPuppetMods(idx, false);
        _spawn.Despawn(idx);
        _puppetGuise.Remove(idx); _puppetTid.Remove(idx); _puppetSpeed.Remove(idx); _puppetScale.Remove(idx);
        if (_selectedPuppet == idx) { _selectedPuppet = ushort.MaxValue; _animApplyToPuppet = false; }
    }

    // ---- 2. subject bar -------------------------------------------------------------------------------------

    private void DrawSubjectBar(ICharacter? self, ICharacter? target)
    {
        var isSelf = !_stageAll && !_animApplyToPuppet;
        var idx = target != null ? (ushort)target.ObjectIndex : ushort.MaxValue;
        var style = ImGui.GetStyle();

        ImGui.AlignTextToFramePadding();
        if (_stageAll)
            ImGui.TextUnformatted($"All puppets · {_spawn.Count}");
        else if (isSelf)
        {
            var worn = _wornGuise?.DisplayName;
            ImGui.TextUnformatted(worn != null ? $"You · disguised as {worn}" : $"You · {self?.Name.TextValue ?? "(not in the world)"}");
        }
        else
            ImGui.TextUnformatted($"{(_puppetGuise.TryGetValue(idx, out var g) ? g.DisplayName : "Blank dummy")} · obj#{idx}");

        // Actions
        if (!_stageAll && !isSelf && target != null)
        {
            ImGui.SameLine();
            var possessing = _possession.IsPossessing && _possession.PossessedIndex == idx;
            if (possessing)
            {
                PushRed();
                if (ImGui.SmallButton("Release")) _possession.Release();
                PopColors();
            }
            else
            {
                using (new Disabled(!_possession.CanPossess(idx)))
                    if (ImGui.SmallButton("Possess")) _possession.Possess(idx);
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip(_possession.CanPossess(idx)
                        ? "Drive it with WASD from an orbit camera. Space up · Ctrl down · Esc release."
                        : "Another player's puppet. Config > Possession can allow driving others' puppets.");
            }
        }
        ImGui.SameLine();
        using (new Disabled(target == null && !_stageAll))
            if (ImGui.SmallButton(isSelf ? "Disguise…" : "Look…")) { _spawnFilter = ""; ImGui.OpenPopup("##stage_look"); }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(isSelf ? "Wear a mob or NPC yourself." : _stageAll ? "Dress every puppet as the same mob or NPC." : "Change what this puppet wears.");
        ImGui.SetNextWindowSize(new Vector2(440, 420), ImGuiCond.Appearing);
        if (ImGui.BeginPopup("##stage_look"))
        {
            DrawLookPicker(spawn: false, self, target);
            ImGui.EndPopup();
        }
        // b44: Dress… opens HOutfits pointed at the subject (sets, pieces, NPC outfits, faces, weapons). Human bodies only
        // (Glamourer dresses humans); one character at a time. Greys out with a reason until HOutfits offers the target.
        ImGui.SameLine();
        var outfits = Outfits?.Available == true;
        var human = target != null && PoseLibrary.BodyOf(target) > 0;
        using (new Disabled(!outfits || _stageAll || !human))
            if (ImGui.SmallButton("Dress…") && target != null && Outfits != null)
            {
                var label = isSelf ? "you" : $"{(_puppetGuise.TryGetValue(idx, out var dg) ? dg.DisplayName : "Blank dummy")} (HDM puppet)";
                if (!Outfits.DressTarget(target.ObjectIndex, label))
                    _log.Information($"Stage: HOutfits declined to dress obj#{target.ObjectIndex}.");
            }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(!outfits ? "Needs HOutfits with the dressing target (requested; coming in an HOutfits update)."
                           : _stageAll ? "Dress one character at a time: pick a cast chip."
                           : !human ? "HOutfits dresses human bodies; this one is a monster or demihuman."
                           : "Open HOutfits on this character: sets, single pieces, NPC outfits, faces and weapons.");
        if (isSelf && self != null && (_guise.IsGuised(self.ObjectIndex) || _humanGuise.IsGuised(self.ObjectIndex)))
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Revert")) RevertGuise(self);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Drop the disguise: your real model and size again.");
        }
        if (!isSelf)
        {
            ImGui.SameLine();
            PushRed();
            var despawn = ImGui.SmallButton(_stageAll ? $"Despawn all ({_spawn.Count})" : "Despawn");
            PopColors();
            if (despawn)
            {
                if (_stageAll) { DespawnAllPuppets(); _puppetGuise.Clear(); _stageAll = false; }
                else DespawnPuppet(idx);
            }
        }

        // Reset, right-aligned
        var resetW = ImGui.CalcTextSize("Reset").X + style.FramePadding.X * 2f;
        ImGui.SameLine(ImGui.GetContentRegionMax().X - resetW);
        PushRed();
        var reset = ImGui.Button("Reset##stage_reset");
        PopColors();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Back to a normal, unlocked idle (clears a stuck or held animation; keeps the look).");
        if (reset) ForSubject(target, ResetToNormal);

        ImGui.Spacing();   // b55: the "Driving …" line moved to the banner above the tabs
    }

    /// <summary>Run an action on the subject: every puppet when "All puppets" is chosen, else the one target.</summary>
    private void ForSubject(ICharacter? target, Action<ICharacter> act)
    {
        if (_stageAll) BroadcastAnim(act);
        else if (target != null) act(target);
    }

    // ---- look / spawn picker ----------------------------------------------------------------------------------

    /// <summary>Favourites + search. spawn = true: each row spawns a puppet wearing it (the new puppet becomes the
    /// subject), plus a blank dummy. spawn = false: each row is worn by the subject (you, the puppet, or all puppets).</summary>
    private void DrawLookPicker(bool spawn, ICharacter? self, ICharacter? target)
    {
        if (spawn)
        {
            if (ImGui.Button("Blank dummy") && self != null)
            {
                if (RequestSpawn(null) != SpawnRequest.Refused) ImGui.CloseCurrentPopup();
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("A plain puppet (a copy of you) a step ahead; dress it with Look.");
            ImGui.SameLine();
        }
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
        ImGui.InputTextWithHint("##pickfilter", "Search name or BaseId…", ref _spawnFilter, 64);

        var favRows = new List<MobRow>();
        foreach (var id in _config.Favorites)
            if (_index.TryGetByKey(id, out var fr) && IsRenderable(fr)) favRows.Add(fr);
        favRows.Sort(static (a, b) =>
        {
            var c = string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : a.BaseId.CompareTo(b.BaseId);
        });
        const int cap = 60;
        var q = _spawnFilter.Trim();
        var matches = MatchSpawnRows(q, cap + 1, _config.Favorites);
        var truncated = matches.Count > cap;
        if (truncated) matches.RemoveAt(matches.Count - 1);

        ImGui.BeginChild("##pickrows", new Vector2(0, 0), true);
        if (favRows.Count > 0)
        {
            ImGui.TextDisabled($"Favourites · {favRows.Count}");
            foreach (var r in favRows) DrawPickRow(r, spawn, self, target);
            ImGui.Separator();
        }
        if (matches.Count == 0)
            ImGui.TextDisabled(q.Length == 0 ? "Type to search the whole catalog." : "No renderable mob matches.");
        else
        {
            foreach (var r in matches) DrawPickRow(r, spawn, self, target);
            if (truncated) ImGui.TextDisabled($"…more than {cap} matches. Refine the search.");
        }
        ImGui.EndChild();
    }

    private void DrawPickRow(MobRow r, bool spawn, ICharacter? self, ICharacter? target)
    {
        ImGui.PushID(unchecked((int)r.RowKey));
        var fav = _config.Favorites.Contains(r.RowKey);
        if (FavStarButton(fav))
        {
            if (fav) _config.Favorites.Remove(r.RowKey); else _config.Favorites.Add(r.RowKey);
            _pi.SavePluginConfig(_config);
            _cachedFilterKey = "\0";
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(fav ? "Unpin from favourites" : "Pin to favourites");
        ImGui.SameLine();
        var verb = spawn ? "Spawn" : "Wear";
        var btnW = ImGui.CalcTextSize(verb).X + ImGui.GetStyle().FramePadding.X * 2f;
        if (ImGui.Selectable($"{r.DisplayName}##pick", _selected?.RowKey == r.RowKey, ImGuiSelectableFlags.None,
                             new Vector2(ImGui.GetContentRegionAvail().X - btnW - ImGui.GetStyle().ItemSpacing.X, 0)))
            _selected = r;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"{r.DisplayName}\n{r.SkeletonCode} · mc{r.ModelCharaId} · #{r.BaseId}");
        ImGui.SameLine();
        if (ImGui.SmallButton(verb))
        {
            _selected = r;
            if (spawn)
            {
                RequestSpawn(r);
            }
            else if (_stageAll)
                BroadcastAnim(c => WearOn(c, r));
            else if (target != null)
            {
                if (self != null && target.ObjectIndex == self.ObjectIndex) ApplyGuise(self, r);
                else WearOn(target, r);
            }
            ImGui.CloseCurrentPopup();
        }
        ImGui.PopID();
    }

    // Dress a puppet, keeping a size the DM dialled for it.
    private void WearOn(ICharacter puppet, MobRow r)
    {
        var idx = (ushort)puppet.ObjectIndex;
        var forced = _puppetScale.TryGetValue(idx, out var ps) ? (float?)ps : null;
        // b48: a new look needs your mods resolved for the NEW model, then frozen again (the guise apply redraws it).
        if (_config.PuppetsUseMyMods && _spawn.IsOriginated(idx)) Mods?.Begin(idx);
        ApplyGuise(puppet, r, forcedScale: forced);
        _puppetGuise[idx] = r;
    }

    // ---- 3. placement & playback ----------------------------------------------------------------------------

    private void DrawSubjectControls(ICharacter? self, ICharacter? target)
    {
        ImGui.SetNextItemOpen(_stagePlacementOpen, ImGuiCond.Once);
        _stagePlacementOpen = ImGui.CollapsingHeader("Placement & playback");
        if (!_stagePlacementOpen) return;
        if (target == null) { ImGui.TextDisabled("Nothing to control: not in the world."); return; }

        var isSelf = !_stageAll && !_animApplyToPuppet;
        var idx = (ushort)target.ObjectIndex;
        var possessingThis = _possession.IsPossessing && _possession.PossessedIndex == idx;
        const float w = 200f;

        // Position + facing (one puppet)
        if (!isSelf && !_stageAll && _spawn.TryGetTransform(idx, out var pos, out var rot))
        {
            using (new Disabled(possessingThis))
            {
                var p = pos;
                ImGui.SetNextItemWidth(w + 60f);
                if (ImGui.DragFloat3("##st_pos", ref p, 0.05f)) { _spawn.SetPosition(idx, p); ReportPuppetMove(idx, p, rot); }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Slide it (X / height / Z). Ctrl+click to type exact coordinates.");
                ImGui.SameLine();
                if (ImGui.SmallButton("Bring to me")) { _spawn.MoveToLocalPlayer(idx); if (_spawn.TryGetTransform(idx, out var bp, out var br)) ReportPuppetMove(idx, bp, br); }
                var yaw = rot;
                ImGui.SetNextItemWidth(w + 60f);
                if (ImGui.SliderFloat("##st_yaw", ref yaw, -MathF.PI, MathF.PI, "facing %.2f")) { _spawn.SetRotation(idx, yaw); ReportPuppetMove(idx, p, yaw); }
                ImGui.SameLine();
                if (ImGui.SmallButton("Face me")) { _spawn.FaceLocalPlayer(idx); if (_spawn.TryGetTransform(idx, out var fp, out var fr)) ReportPuppetMove(idx, fp, fr); }
            }
            if (possessingThis) ImGui.TextDisabled("Driving: WASD moves and steers it. Release to place it by hand.");
        }

        // Scale (puppets; your own size lives in the Catalog)
        if (!isSelf)
        {
            var scl = _stageAll ? _puppetScaleAll : _puppetScale.TryGetValue(idx, out var scv) ? scv : _guise.GetScale(target);
            ImGui.SetNextItemWidth(w);
            if (ImGui.SliderFloat("##st_scale", ref scl, 0.1f, 10f, "scale x%.2f"))
            {
                if (_stageAll) { _puppetScaleAll = scl; BroadcastAnim(a => { _puppetScale[(ushort)a.ObjectIndex] = scl; ApplyScaleTo(a, scl, commit: false); }); }
                else { _puppetScale[idx] = scl; ApplyScaleTo(target, scl, commit: false); }
            }
            if (ImGui.IsItemDeactivatedAfterEdit()) ForSubject(target, a => ApplyScaleTo(a, scl, commit: true));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Resize (0.1x to 10x). A monster's size settles on its next Look change.");
            ImGui.SameLine();
            if (ImGui.SmallButton("1x##st_scale")) ForSubject(target, a => { _puppetScale[(ushort)a.ObjectIndex] = 1f; ApplyScaleTo(a, 1f, commit: true); });
            ImGui.SameLine();
        }

        // Elevation
        var voff = _guise.GetVerticalOffset(idx);
        ImGui.SetNextItemWidth(w);
        if (ImGui.SliderFloat("##st_elev", ref voff, -20f, 20f, "elevation %+.2f")) ForSubject(target, a => ApplyElevationTo(a, voff, commit: false));
        if (ImGui.IsItemDeactivatedAfterEdit()) ForSubject(target, a => ApplyElevationTo(a, voff, commit: true));
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Raise or lower where the model is drawn (the character itself doesn't move).");
        ImGui.SameLine();
        if (ImGui.SmallButton("0##st_elev")) ForSubject(target, a => ApplyElevationTo(a, 0f, commit: true));

        // Speed + freeze
        var frozen = _anim.IsFrozen(idx);
        if (ImGui.Checkbox("Freeze", ref frozen))
            ForSubject(target, a => { _puppetSpeed[(ushort)a.ObjectIndex] = frozen ? 0f : 1f; ApplyFreeze(a, frozen); });
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Hold on a still frame. Untick to resume.");
        ImGui.SameLine();
        var spd = _puppetSpeed.TryGetValue(idx, out var sv) ? sv : 1f;
        ImGui.SetNextItemWidth(w - 60f);
        if (ImGui.SliderFloat("##st_speed", ref spd, 0f, 3f, "speed %.2f"))
            ForSubject(target, a => { _puppetSpeed[(ushort)a.ObjectIndex] = spd; _anim.SetSpeed(a, spd); });
        ImGui.SameLine();
        if (ImGui.SmallButton("1x##st_speed")) ForSubject(target, a => { _puppetSpeed[(ushort)a.ObjectIndex] = 1f; _anim.SetSpeed(a, 1f); });

        // Stances + weapon
        var drawn = _anim.IsHoldingLoop(target, LocomotionData.BtlIdle);
        if (ImGui.SmallButton(drawn ? "Sheathe weapon" : "Draw weapon"))
            ForSubject(target, a =>
            {
                if (drawn) StopAnim(a);
                else { _anim.Loop(a, LocomotionData.BtlIdle); ReportHeldLoop(a, LocomotionData.BtlIdle); }
            });
        ImGui.SameLine();
        ushort dieIntro = (ushort)_timeline.ResolveCommonId("battle/dead");
        ushort dieHold = (ushort)_timeline.ResolveCommonId("battle/dead_pose");
        using (new Disabled(dieIntro == 0 || dieHold == 0))
            if (ImGui.SmallButton("Die")) ForSubject(target, a => { _anim.PlaySequence(a, dieIntro, dieHold); ReportHeldLoop(a, dieHold, dieIntro); });
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("The death fall, then lie still. Reset (or walking, on yourself) gets up.");
        ImGui.SameLine();
        if (ImGui.SmallButton("Cycle pose")) ForSubject(target, a => _anim.CyclePose(a));
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Step through the standing idle variants and hold one.");
        if (!isSelf)
        {
            ImGui.SameLine();
            var shown = _guise.GetWeaponDrawn(idx);
            if (ImGui.Checkbox("Show weapon", ref shown))
                ForSubject(target, a =>
                {
                    if (_humanGuise.IsGuised(a.ObjectIndex)) _humanGuise.SetWeaponShown(a.ObjectIndex, shown);
                    else _guise.SetWeaponDrawn(a.ObjectIndex, shown);
                });
        }

        // Raw timeline id (power users)
        var tid = _puppetTid.TryGetValue(idx, out var t) ? t : _timelineId;
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled("Timeline id");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(70);
        if (ImGui.InputInt("##st_tid", ref tid, 0)) { _puppetTid[idx] = tid; _timelineId = tid; }
        ImGui.SameLine();
        using (new Disabled(tid is <= 0 or > ushort.MaxValue))
        {
            if (ImGui.SmallButton("Play##st_tid")) ForSubject(target, a => { _anim.PlayOnce(a, (ushort)tid); ReportOneShot(a, (ushort)tid); });
            ImGui.SameLine();
            if (ImGui.SmallButton("Loop##st_tid")) ForSubject(target, a => { _anim.Loop(a, (ushort)tid); ReportHeldLoop(a, (ushort)tid); });
        }
        ImGui.Spacing();
    }

    // ---- helpers kept from the old Spawn tab ------------------------------------------------------------------

    /// <summary>Renderable catalog rows matching a search (name or BaseId, case-insensitive), capped, minus any
    /// pinned-favourite ids (those show once, pinned above). Empty query returns nothing.</summary>
    private List<MobRow> MatchSpawnRows(string query, int cap, HashSet<uint>? exclude = null)
    {
        var results = new List<MobRow>();
        if (query.Length == 0) return results;
        var byId = uint.TryParse(query, out var qid);
        foreach (var r in _index.Rows)
        {
            if (!IsRenderable(r)) continue;
            if (exclude != null && exclude.Contains(r.RowKey)) continue;
            if (!(r.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                  || (byId && r.BaseId == qid)
                  || r.BaseId.ToString().Contains(query)))
                continue;
            results.Add(r);
            if (results.Count >= cap) break;
        }
        return results;
    }

    /// <summary>b48 (F-02): the DM's Penumbra mods on their puppets, frozen per puppet (see PuppetMods). Set by Plugin.</summary>
    public PuppetMods? Mods { get; set; }

    /// <summary>Penumbra (redraw), set by Plugin.</summary>
    public PenumbraIpc? Penumbra { get; set; }

    /// <summary>b46/b48 (F-02): a puppet the DM just spawned gets the DM's mods (frozen ~2 s later); off / despawn undoes it.
    /// Always called on spawn, also when off, because puppet names repeat across sessions and a stale assignment must not
    /// linger.</summary>
    private void SetPuppetMods(int idx, bool on)
    {
        if (Mods == null) return;
        if (on) Mods.Begin(idx); else Mods.End(idx, redraw: false, announce: false);   // b51: despawn / spawn paths stay silent
    }

    private void DespawnAllPuppets()
    {
        _spawnQueue.Clear();
        foreach (var idx in _spawn.Spawned.ToList()) SetPuppetMods(idx, false);
        _spawn.DespawnAll();
    }

    // ---- spawn queue (DM: "a fast clicker cannot flood the lane with 10 prop spawns per second") -----------------
    // Every spawn the DM asks for goes through RequestSpawn. While HM-Sync is running, spawns leave at most one per
    // SpawnGapMs: each spawn sends several sync messages (spawn, position, look, the position again when ready, mods at
    // ~2 s, a top-up at ~6 s), and a burst reordered on the wire left peers with misplaced or orphan copies. Extra clicks
    // are QUEUED, not dropped (a DM who double-clicks three props expects three), up to MaxQueuedSpawns. Without HM-Sync
    // nothing waits: a purely local spawn has no lane to protect. Peers' mirror spawns never pass through here.
    public enum SpawnRequest { Done, Queued, Refused }
    private readonly Queue<MobRow?> _spawnQueue = new();
    private long _nextSpawnAt;
    private const int SpawnGapMs = 1000;
    private const int MaxQueuedSpawns = 10;

    /// <summary>b54: HM-Sync is running (spawns are paced). Set by Plugin.</summary>
    public Func<bool>? SyncRunning { get; set; }

    /// <summary>Spawn <paramref name="row"/> (null = a blank dummy) now, or queue it behind the pacing gap.</summary>
    private SpawnRequest RequestSpawn(MobRow? row)
    {
        if (Self() == null) return SpawnRequest.Refused;
        var paced = SyncRunning?.Invoke() == true;
        if (!paced || (_spawnQueue.Count == 0 && Environment.TickCount64 >= _nextSpawnAt))
        {
            SpawnNow(row, paced);
            return SpawnRequest.Done;
        }
        if (_spawnQueue.Count >= MaxQueuedSpawns)
        {
            _log.Information($"Spawn: queue full ({MaxQueuedSpawns}); request refused.");
            return SpawnRequest.Refused;
        }
        _spawnQueue.Enqueue(row);
        return SpawnRequest.Queued;
    }

    private void SpawnNow(MobRow? row, bool paced)
    {
        var p = row is null ? SpawnBlankPuppet() : SpawnPuppetAs(row);
        if (p != null) SelectPuppet((ushort)p.ObjectIndex);   // the new puppet becomes the Stage's subject
        if (paced) _nextSpawnAt = Environment.TickCount64 + SpawnGapMs;
    }

    /// <summary>Called every frame by Plugin (the window may be closed): release the next queued spawn when due.</summary>
    public void PumpSpawnQueue()
    {
        if (_spawnQueue.Count == 0 || Environment.TickCount64 < _nextSpawnAt) return;
        if (Self() == null) { _spawnQueue.Clear(); return; }   // logged out / zoning: drop the queue
        SpawnNow(_spawnQueue.Dequeue(), SyncRunning?.Invoke() == true);
    }

    /// <summary>Run an action on every live puppet. Resolves each puppet's ICharacter fresh.</summary>
    private void BroadcastAnim(Action<ICharacter> act)
    {
        foreach (var idx in _spawn.Spawned.ToList())
            if (_objects[idx] is ICharacter c)
                act(c);
    }

    /// <summary>Drop per-puppet UI state for indexes that are no longer live puppets (a zone change cleared them), so a
    /// recycled index can't inherit a stale label, timeline id, speed or size.</summary>
    private void PrunePuppetLabels()
    {
        if (_puppetGuise.Count == 0 && _puppetTid.Count == 0 && _puppetSpeed.Count == 0 && _puppetScale.Count == 0) return;
        var live = _spawn.Spawned;
        foreach (var key in _puppetGuise.Keys.ToList()) if (!live.Contains(key)) _puppetGuise.Remove(key);
        foreach (var key in _puppetTid.Keys.ToList())   if (!live.Contains(key)) _puppetTid.Remove(key);
        foreach (var key in _puppetSpeed.Keys.ToList()) if (!live.Contains(key)) _puppetSpeed.Remove(key);
        foreach (var key in _puppetScale.Keys.ToList()) if (!live.Contains(key)) _puppetScale.Remove(key);
    }

    /// <summary>"All puppets": a library click plays on every puppet (each through the usual funnel, so peers and
    /// held-pose sync behave exactly as for one). Returns true when it handled the broadcast.</summary>
    private bool BroadcastIfAll(Action<ICharacter> play)
    {
        if (!_stageAll || _stageBroadcasting) return false;
        _stageBroadcasting = true;
        try { BroadcastAnim(play); }
        finally { _stageBroadcasting = false; }
        return true;
    }
}
