#!/usr/bin/env py -3
"""
Generates Data/props.csv (columns: Name,Set,Base,Variant) - hand props for the pose library (b21).

WHY: NPC poses that hold something (drink, smoke, read, sweep...) animate the hand holding whatever model is in
the MAIN-HAND weapon slot; the game's cutscene props are weapon-slot models. Loading one of these into that slot
(GuiseService.SetPoseHand) puts a cup / pipe / book in the hand instead of the actor's weapon.

Source: Anamnesis (MIT licence, (c) Anamnesis contributors) Anamnesis/Data/Equipment.json - its named
weapon-slot props, written there as "Set, Base, Variant". Kept: weapon-slot entries whose Set is a 9xxx prop
set; deduplicated by model (first name wins).
"""
import csv, json, os, re, zipfile

HERE = os.path.dirname(os.path.abspath(__file__))   # this Data folder; inputs default to sibling checkouts, override with env vars
ANAM = os.environ.get("ANAMNESIS_ZIP", os.path.join(HERE, "..", "..", "_GitHub", "Anamnesis-master.zip"))
OUT  = os.path.join(HERE, "props.csv")

with zipfile.ZipFile(ANAM) as z:
    eq = json.loads(z.read("Anamnesis-master/Anamnesis/Data/Equipment.json").decode("utf-8-sig"))

seen, rows = set(), []
for e in eq:
    if e.get("Slot") != "Weapons":
        continue
    parts = [p.strip() for p in str(e.get("Id", "")).split(",")]
    if len(parts) != 3 or not all(p.isdigit() for p in parts):
        continue
    s, b, v = (int(p) for p in parts)
    if not 9000 <= s <= 9999 or (s, b, v) in seen:
        continue
    name = re.sub(r"\s+", " ", e.get("Name", "")).strip()
    if not name:
        continue
    seen.add((s, b, v))
    rows.append((name, s, b, v))

rows.sort(key=lambda r: r[0].lower())
with open(OUT, "w", newline="", encoding="utf-8") as f:
    f.write("# Hand props for HDM poses. Source: Anamnesis Data/Equipment.json (MIT licence, (c) Anamnesis contributors).\n")
    w = csv.writer(f)
    w.writerow(["Name", "Set", "Base", "Variant"])
    w.writerows(rows)
print(f"{len(rows)} props -> {OUT}")
