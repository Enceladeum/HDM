#!/usr/bin/env py -3
"""
Generates Data/boss-skill-names.csv (columns: Skel,Id,Name) - BOSS-SCOPED names for monster special
timelines, keyed by (skeleton, ActionTimeline id).

WHY scoped: modern bosses play their attacks through GENERIC template rows whose key carries a
placeholder (ActionTimeline 3216 = "mon_sp/[SKL_ID]/mon_sp016"); the engine substitutes the caster's
skeleton at play time. The same id is therefore reused by every boss, so a global Action-sheet join
(id -> one name) mislabels it. Scoping by caster fixes that: 3216 on m0692 (Diamond Weapon) is
"Flood Ray / Diamond Rain / ...", and on some other skeleton something else entirely.

Source of the scoping: the BossMod encounter modules (ffxiv_bossmod-master.zip). Each module declares
  enum OID : uint { Boss = 0x4190, ... }               -> BNpcBase ids of the encounter's actors
  enum AID : uint { Hydrowave = 36493, // Boss->self   -> Action ids + WHO casts them (before "->")
Join: AID caster token -> OID -> BNpcBase.ModelChara -> ModelChara (Type 3 = monster) -> mNNNN skeleton;
      AID -> Action.AnimationEnd -> ActionTimeline id, kept only when its Key starts "mon_sp/"
      (both template "[SKL_ID]" rows and older literal per-skeleton rows).
Helper/invisible casters (the VFX "Helper" actor) are skipped by construction: they have no monster
model of their own, so they never resolve to a skeleton.

Rules:
  - Name = the Action sheet's Name (localized, canonical); falls back to the AID identifier split on
    case when the sheet name is blank.
  - Several attacks sharing one body animation are ALL listed ("A / B / C"), capped at 4 + "+N more".
    That is a real fact about the data (one animation slot, several attacks), not ambiguity to hide.
  - _hit helper timelines are excluded (damage-reaction sub-timelines, never a button).

Datamining CSVs here have a SINGLE header row; columns are looked up by name.
"""
import csv, os, re, sys, zipfile, collections

HERE = os.path.dirname(os.path.abspath(__file__))   # this Data folder; inputs default to sibling checkouts, override with env vars
DM   = os.environ.get("FFXIV_DATAMINING_CSV", os.path.join(HERE, "..", "..", "_GitHub", "ffxiv-datamining-master", "csv", "en"))
BMZ  = os.environ.get("BOSSMOD_ZIP", os.path.join(HERE, "..", "..", "_GitHub", "ffxiv_bossmod-master.zip"))
OUT  = os.path.join(HERE, "boss-skill-names.csv")

def sheet(name):
    with open(os.path.join(DM, name), encoding="utf-8") as f:
        r = csv.reader(f)
        hdr = next(r)
        return hdr, [row for row in r if row and row[0].isdigit()]

# ActionTimeline: id -> Key
h, at = sheet("ActionTimeline.csv"); ki = h.index("Key")
tl_key = {int(r[0]): r[ki] for r in at}

# Action: id -> (Name, AnimationEnd)
h, ac = sheet("Action.csv"); ni, ae = h.index("Name"), h.index("AnimationEnd")
action = {int(r[0]): (r[ni].strip(), int(r[ae] or 0)) for r in ac}

# BNpcBase -> ModelChara ; ModelChara -> (Type, Model)
h, bb = sheet("BNpcBase.csv"); mi = h.index("ModelChara")
base_mc = {int(r[0]): int(r[mi] or 0) for r in bb}
h, mc = sheet("ModelChara.csv"); ti, mo = h.index("Type"), h.index("Model")
mc_skel = {int(r[0]): f"m{int(r[mo]):04d}" for r in mc if r[ti] == "3" and int(r[mo] or 0) > 0}

def split_ident(s):
    return re.sub(r"(?<=[a-z0-9])(?=[A-Z])", " ", s).strip()

# ---- parse BossMod: group enums by namespace (enums often live in a sibling *Enums.cs) ----
ns_oid = collections.defaultdict(dict)   # namespace -> {OidName: baseId}
ns_aid = collections.defaultdict(list)   # namespace -> [(AidName, actionId, [casterTokens])]
enum_re = re.compile(r"enum\s+(OID|AID)\s*:\s*uint\s*\{(.*?)\n\}", re.S)
line_re = re.compile(r"^\s*(\w+)\s*=\s*(0x[0-9A-Fa-f]+|\d+)\s*,?\s*(?://\s*(.*))?$")
with zipfile.ZipFile(BMZ) as z:
    for n in z.namelist():
        if "/Modules/" not in n or not n.endswith(".cs"):
            continue
        s = z.read(n).decode("utf-8-sig", "replace")
        m = re.search(r"^\s*namespace\s+([\w.]+)", s, re.M)
        if not m:
            continue
        ns = m.group(1)
        for em in enum_re.finditer(s):
            kind, body = em.group(1), em.group(2)
            for ln in body.splitlines():
                lm = line_re.match(ln)
                if not lm:
                    continue
                ident, val, cmt = lm.group(1), int(lm.group(2), 0), lm.group(3) or ""
                if kind == "OID":
                    ns_oid[ns][ident] = val
                else:
                    src = cmt.split("->", 1)[0] if "->" in cmt else ""
                    ns_aid[ns].append((ident, val, [t.strip() for t in src.split("/") if t.strip()]))

# ---- join ----------------------------------------------------------------------------------
names = collections.defaultdict(list)    # (skel, tlId) -> [names in first-seen order]
stats = collections.Counter()
for ns, aids in ns_aid.items():
    oids = ns_oid.get(ns, {})
    for ident, aid, casters in aids:
        act = action.get(aid)
        if not act:
            stats["aid-not-in-sheet"] += 1; continue
        aname, tl = act
        key = tl_key.get(tl, "")
        if not key.startswith("mon_sp/") or "_hit" in key:
            continue
        label = aname or split_ident(ident)
        for c in casters:
            base = oids.get(c)
            if base is None:
                continue
            skel = mc_skel.get(base_mc.get(base, 0))
            if not skel:
                continue
            # A literal row only belongs to its own skeleton; a template row belongs to the caster's.
            if "[SKL_ID]" not in key and f"/{skel}/" not in key:
                continue
            lst = names[(skel, tl)]
            if label not in lst:
                lst.append(label)
                stats["pairs"] += 1

with open(OUT, "w", newline="", encoding="utf-8") as f:
    w = csv.writer(f)
    w.writerow(["Skel", "Id", "Name"])
    for (skel, tl) in sorted(names):
        lst = names[(skel, tl)]
        label = " / ".join(lst[:4]) + (f" +{len(lst) - 4} more" if len(lst) > 4 else "")
        w.writerow([skel, tl, label])

tmpl = sum(1 for (s, t) in names if "[SKL_ID]" in tl_key.get(t, ""))
print(f"namespaces {len(ns_aid)} | (skel,timeline) rows {len(names)} "
      f"({tmpl} template, {len(names) - tmpl} literal) | skeletons {len({s for s, _ in names})} | {dict(stats)}")
print(f"-> {OUT}")
