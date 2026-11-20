#!/usr/bin/env py -3
"""
Generates Data/mob-name-pairs.csv (columns: BaseId,NameId,Name,Primary,Source) - EVERY known name for each
BNpcBase, plus which one is the row's primary label.

WHY: a BNpcBase is a shared BODY, not a mob. The server pairs it with a BNpcName at spawn (that pair is
what the nameplate shows) and the same body ships under many names - base 12 is northern vulture, arbor
buzzard, chasm buzzard, ... mob-model-index.csv keeps ONE name per base, so every other mob on that body
was either mislabelled or missing (and a dungeon add sharing an overworld body was filed under the
overworld name, so it vanished from its dungeon category). No client file stores the pairing, so it can
only come from crowdsourced spawn data.

Sources:
  - mob-model-index.csv (HDM's existing crowdsourced pairing)
  - Penumbra.GameData Data/BNpcNames.cs (community crowdsource + LuminaSupplemental + BnpcPairs, generated 2026-09-04):
    base -> ALL known BNpcName ids
  - mob-territory-instanced.csv (BossMod-derived roster): base -> boss NameId and BossMod's own OID label
HDM's old pairing is systematically MIS-JOINED where it disagrees with Penumbra: on every disagreement
the BossMod evidence can decide (279 bases), it sides with Penumbra, never HDM (e.g. 17831 HDM
"Lunipyati" vs BossMod FRU BossP3 = Oracle of Darkness; 18677 "traverse rider" vs Malacoda; 19218
"North Shroud lemur" vs Lone Swordmaster, which the live harvester also confirmed). So when Penumbra
has names for a base and HDM's name is NOT among them, HDM's name is DROPPED (keeping it would put a
wrong label on a real row). Primary choice, in order:
  1. the BossMod roster's NameId for this base (the encounter module names it)
  2. a candidate whose name equals BossMod's OID label, alnum-normalized ("IhuykatumuFlytrap")
  3. HDM's existing name, when it survived the mis-join check (unchanged behaviour)
  4. Penumbra's first name
Every other distinct name (case-insensitive) becomes an alias row at runtime.
"""
import csv, os, re, zipfile, collections

HERE = os.path.dirname(os.path.abspath(__file__))   # this Data folder; inputs default to sibling checkouts, override with env vars
HDM  = HERE
DM   = os.environ.get("FFXIV_DATAMINING_CSV", os.path.join(HERE, "..", "..", "_GitHub", "ffxiv-datamining-master", "csv", "en"))
PGD  = os.environ.get("PENUMBRA_GAMEDATA_ZIP", os.path.join(HERE, "..", "..", "_GitHub", "Penumbra.GameData-main.zip"))
OUT  = os.path.join(HDM, "mob-name-pairs.csv")

norm = lambda s: re.sub(r"[^a-z0-9]", "", s.lower())

with zipfile.ZipFile(PGD) as z:
    t = z.read("Penumbra.GameData-main/Data/BNpcNames.cs").decode("utf-8", "replace")
body = t[t.index("// @formatter:off"):]
pen = [[int(x) for x in re.findall(r"\d+", l)] for l in re.findall(r"\[([^\[\]]*)\]", body)]

with open(os.path.join(DM, "BNpcName.csv"), encoding="utf-8") as f:
    r = csv.reader(f); hdr = next(r); si = hdr.index("Singular")
    bname = {int(x[0]): x[si].strip() for x in r if x and x[0].isdigit()}

with open(os.path.join(HDM, "mob-model-index.csv"), encoding="utf-8") as f:
    hdm = {int(x["BaseId"]): (int(x["NameId"] or 0), x["Name"].strip()) for x in csv.DictReader(f)}

roster = collections.defaultdict(list)   # base -> [(nameId, label)]
with open(os.path.join(HDM, "mob-territory-instanced.csv"), encoding="utf-8") as f:
    for x in csv.DictReader(f):
        roster[int(x["BaseId"])].append((int(x["NameId"] or 0), x["Label"]))

stats = collections.Counter()
out = []
for base, (hnid, hname) in sorted(hdm.items()):
    cands = collections.OrderedDict()            # lower name -> [nameId, name, sources]
    def add(nid, name, src):
        if not name: return
        k = name.lower()
        if k in cands:
            if src not in cands[k][2]: cands[k][2].append(src)
        else:
            cands[k] = [nid, name, [src]]
    plist = pen[base] if base < len(pen) else []
    pnames = {bname.get(n, "").lower() for n in plist} - {""}
    if hname and pnames and hname.lower() not in pnames:
        stats["hdm-misjoin-dropped"] += 1
        hname = ""                                            # mis-joined: don't carry it forward
    add(hnid, hname, "hdm")
    for nid in plist:
        add(nid, bname.get(nid, ""), "pen")
    for nid, _ in roster.get(base, []):
        if nid: add(nid, bname.get(nid, ""), "roster")
    if not cands:
        continue

    primary = None
    for nid, _ in roster.get(base, []):                       # 1. roster NameId
        if nid and bname.get(nid, "").lower() in cands:
            primary = bname[nid].lower(); stats["primary:roster-name"] += 1; break
    if primary is None:                                       # 2. OID label match
        labels = {norm(l) for _, l in roster.get(base, [])}
        for k, (nid, name, _) in cands.items():
            if norm(name) in labels:
                primary = k; stats["primary:oid-label"] += 1; break
    if primary is None and hname:                             # 3. HDM's existing
        primary = hname.lower(); stats["primary:hdm"] += 1
    if primary is None:                                       # 4. first crowdsource
        primary = next(iter(cands)); stats["primary:pen-first"] += 1
    if hname and primary != hname.lower():
        stats["primary-changed-from-hdm"] += 1
    if not hname:
        stats["blank-filled"] += 1
    if len(cands) > 1:
        stats["bases-with-aliases"] += 1
        stats["alias-rows"] += len(cands) - 1

    for k, (nid, name, src) in cands.items():
        out.append([base, nid, name, 1 if k == primary else 0, "+".join(src)])

with open(OUT, "w", newline="", encoding="utf-8") as f:
    w = csv.writer(f)
    w.writerow(["BaseId", "NameId", "Name", "Primary", "Source"])
    w.writerows(out)
print(dict(stats)); print(f"{len(out)} rows -> {OUT}")
