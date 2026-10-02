"""Vergelijkt tijdschrijven.REPAIRED.json met het origineel (Stap 2)."""
import json
import os
import sys
from collections import Counter

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ORIG = sys.argv[1] if len(sys.argv) > 1 else os.path.join(ROOT, "tijdschrijven.json")
REP = os.path.join(ROOT, "tijdschrijven.REPAIRED.json")
LISTS = ["projecten", "werkzaamheden", "bestemmingen", "inactieveProjecten"]
ok = True


def check(naam, cond, extra=""):
    global ok
    ok &= bool(cond)
    print(("OK    " if cond else "FOUT  ") + naam + (f"  {extra}" if extra else ""))


def mojibake(s):
    return isinstance(s, str) and ("Ã" in s or "Â" in s)


def same_except_fixed(a, b):
    """Gelijk, of a was mojibake-string (b is de gerepareerde variant)."""
    return a == b or (isinstance(a, str) and isinstance(b, str) and mojibake(a))


size = os.path.getsize(REP)
check("kleiner dan 2 MB", size < 2 * 1024 * 1024, f"({size} bytes)")
raw = open(REP, "rb").read()
check("geen BOM", not raw.startswith(b"\xef\xbb\xbf"))
b = json.loads(raw.decode("utf-8"))
check("geldige JSON", True)
a = json.load(open(ORIG, "r", encoding="utf-8-sig"))

check("version gelijk", a.get("version") == b.get("version"), f"({a.get('version')!r})")
check("sleutels op topniveau gelijk", set(a) == set(b), f"({sorted(a)})")
ea, eb = a["entries"], b["entries"]
check("aantal entries", len(ea) == len(eb), f"(orig {len(ea)}, nieuw {len(eb)})")
ids_a = [e["id"] for e in ea]
cnt = Counter(e["id"] for e in eb)
check("elke originele id precies 1x", all(cnt[i] == 1 for i in ids_a))
check("geen dubbele ids in het geheel", all(v == 1 for v in cnt.values()))
bmap = {e["id"]: e for e in eb}
gewijzigd = 0
for e in ea:
    n = bmap.get(e["id"])
    if n is None or set(e) != set(n):
        check(f"velden entry {e['id']}", False)
        continue
    for k in e:
        if e[k] != n[k]:
            gewijzigd += 1
            check(f"entry {e['id']} veld {k} alleen gerepareerd", same_except_fixed(e[k], n[k]),
                  f"({len(e[k])} -> {len(n[k])} tekens)")
check("geen mojibake meer in entries", not any(mojibake(v) for e in eb for v in e.values()))
for name in LISTS:
    la, lb = a.get(name, []), b.get(name, [])
    check(f"lijst {name} even lang en inhoud gelijk",
          len(la) == len(lb) and all(same_except_fixed(x, y) for x, y in zip(la, lb)),
          f"({len(la)} items)")
print(f"\nGewijzigde velden in entries: {gewijzigd}")
print("\nALLES KLOPT" if ok else "\nER IS IETS FOUT — NIET DOORGAAN")
sys.exit(0 if ok else 1)
