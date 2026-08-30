#!/usr/bin/env py -3
"""
Generates Data/common-skill-names.csv (columns: Id,Name) — the offline join that
relabels HDM's shared "Common" timeline chips with the recognisable player-skill
name that triggers them, in place of the lossy key-path heuristic (Prettify).

WHY offline: identical cadence to special-names.csv / skel-anim-caps.csv
(regenerated per patch). Bakes ALL the safety rules into the generator
so the runtime loader is a dead-simple id->name lookup.

The animation data model (0-indexed Action.csv columns, verified against the game):
  col1=Name  col15=AnimationEnd(->ActionTimeline)  col18=AnimationStart(->ActionCastTimeline)
  col33=ClassJobCategory
An action's CAST POSE = AnimationStart -> ActionCastTimeline row -> its col1 -> ActionTimeline id.
An action's SWING/HIT  = AnimationEnd -> ActionTimeline id directly.

Safety rules (each prevents a concrete mislabel):
  1. UNIQUE ONLY. A timeline named only if exactly ONE class skill maps to it. (magic_thm_start
     is shared by 140 thaumaturge spells -> excluded; magic_pt11_start -> only Hammer Motif -> kept.)
  2. JOB-AFFILIATED (ClassJobCategory != 0) COMBAT POSES ONLY (key starts "battle/"). This is the
     user's ask ("class skills") and it surgically drops the normal/* grab-bag — item fragments
     ("a Realm Reborn"), duty gadgets (cannon "Pitch Bomb"), telepo — that merely share a pose.
     (This fork's Action.csv has no clean IsPlayerAction column; the battle/ + job gate is the
     principled equivalent and needs no hand-curation.)
  3. EXCLUDE battle/mon_sp* GENERIC SLOTS. Those slot-lettered specials mean a DIFFERENT attack
     per monster; a single action referencing slot C ("Coiled Strike") must NOT rename the generic
     slot-C row globally. Standalone per-skeleton specials get names from special-names.csv.
  4. START/LOOP SIBLINGS. When battle/magic_XXX_start earns a name, its battle/magic_XXX_loop
     sibling (the sustained cast pose the user actually holds) becomes "<name> (loop)".
"""
import csv, collections, os

BASE = r"F:\Unlimited Code Works\FFXIV\_GitHub\ffxiv-datamining-master\csv\en"
HG   = r"F:\Unlimited Code Works\FFXIV\HDM\Data\timeline-index.csv"
OUT  = r"F:\Unlimited Code Works\FFXIV\HDM\Data\common-skill-names.csv"

def rows(name):
    with open(os.path.join(BASE, name), encoding="utf-8") as f:
        r = csv.reader(f)
        next(r); next(r); next(r)  # 3-row header: indices, names, types
        for row in r:
            if row: yield row

# ActionCastTimeline: col0=id -> col1=ActionTimeline id (the cast pose)
cast = {}
for row in rows("ActionCastTimeline.csv"):
    try: cast[int(row[0])] = int(row[1])
    except: pass

# timeline id -> set of distinct class-skill names, split by path
by_end  = collections.defaultdict(set)   # AnimationEnd  (weaponskill swing) — most specific
by_cast = collections.defaultdict(set)   # AnimationStart cast pose
for row in rows("Action.csv"):
    try:
        name = row[1]
        if not name: continue
        anEnd = int(row[15]); anStart = int(row[18]); cjc = int(row[33])
    except: continue
    if cjc == 0:                          # rule 2: job-affiliated only
        continue
    if anStart in cast:
        by_cast[cast[anStart]].add(name)
    if anEnd:
        by_end[anEnd].add(name)

def unique_name(tid):
    """Rule 1: a name only when exactly one job skill maps to this timeline (end preferred)."""
    e = by_end.get(tid)
    if e and len(e) == 1: return next(iter(e))
    c = by_cast.get(tid)
    if c and len(c) == 1: return next(iter(c))
    return None

# HDM Common rows (id, key)
common = []
with open(HG, encoding="utf-8") as f:
    r = csv.reader(f); next(r)
    for row in r:
        if not row: continue
        if row[1] == "common":
            common.append((int(row[0]), row[3]))
id2key = dict(common)
key2id = {k: i for i, k in common}

emit = {}   # id -> name
for tid, key in common:
    if not key.startswith("battle/"):      # rule 2: combat poses only (drops normal/* grab-bag)
        continue
    if key.startswith("battle/mon_sp"):    # rule 3: generic per-monster slots
        continue
    nm = unique_name(tid)
    if nm:
        emit[tid] = nm

# rule 4: start/loop sibling propagation (battle/magic_* family only, where the convention holds)
for tid in list(emit.keys()):
    key = id2key[tid]
    if key.startswith("battle/magic_") and key.endswith("_start"):
        lid = key2id.get(key[:-6] + "_loop")
        if lid is not None and lid not in emit:
            emit[lid] = emit[tid] + " (loop)"

with open(OUT, "w", encoding="utf-8", newline="") as f:
    w = csv.writer(f)
    w.writerow(["Id", "Name"])
    for tid in sorted(emit):
        w.writerow([tid, emit[tid]])

print(f"wrote {len(emit)} skill names -> {OUT}\n")

# ---- diagnostics ----
print("=== Pictomancer motif sanity (the user's flagged case) ===")
for tid, key in sorted(common):
    if "magic_pt" in key and (tid in emit or by_cast.get(tid)):
        cands = by_cast.get(tid)
        print(f"  {key:26} id={tid:6} emit={emit.get(tid)!r:22} candidates={sorted(cands) if cands else None}")

print("\n=== full emit list ===")
for tid in sorted(emit):
    print(f"  {tid:6} {id2key[tid]:34} -> {emit[tid]}")
