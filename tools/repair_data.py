"""Repareert dubbel-gecodeerde tekst (mojibake) in tijdschrijven.json.

Gebruik:  python tools/repair_data.py [invoer.json]
Schrijft naar tijdschrijven.REPAIRED.json; het origineel wordt NOOIT aangepast.
Voegt entries uit nieuwe_regels.json toe als hun id nog niet bestaat.
"""
import json
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = sys.argv[1] if len(sys.argv) > 1 else os.path.join(ROOT, "tijdschrijven.json")
DST = os.path.join(ROOT, "tijdschrijven.REPAIRED.json")
NEW = os.path.join(ROOT, "nieuwe_regels.json")
LISTS = ["projecten", "werkzaamheden", "bestemmingen", "inactieveProjecten"]


def encode_cp1252(s):
    # Eigen encoder: cp1252, met latin-1 voor tekens die cp1252 niet kent.
    out = bytearray()
    for ch in s:
        try:
            out += ch.encode("cp1252")
        except UnicodeEncodeError:
            out += ch.encode("latin-1")  # kan falen -> wordt hieronder opgevangen
    return bytes(out)


def fast_encode(s):
    try:
        return s.encode("cp1252")
    except UnicodeEncodeError:
        return encode_cp1252(s)


def fix(s):
    """Draai mojibake terug zolang 'Ã' of 'Â' voorkomt. Geeft (tekst, stappen)."""
    steps = 0
    while "Ã" in s or "Â" in s:
        try:
            new = fast_encode(s).decode("utf-8")
        except (UnicodeEncodeError, UnicodeDecodeError):
            break
        if new == s:
            break
        s = new
        steps += 1
    return s, steps


def short(s, n=200):
    return s if len(s) <= n else s[:n] + "…"


def main():
    with open(SRC, "r", encoding="utf-8-sig") as f:  # accepteert BOM
        data = json.load(f)

    changes = []

    def repair_field(label, container, key):
        old = container[key]
        if not isinstance(old, str):
            return
        new, steps = fix(old)
        if new != old:
            container[key] = new
            changes.append((label, len(old), len(new), steps, new))

    for e in data.get("entries", []):
        for k in list(e.keys()):
            repair_field(f"entry {e.get('datum')} | {e.get('project')} | {k}", e, k)
    for name in LISTS:
        lst = data.get(name)
        if isinstance(lst, list):
            for i in range(len(lst)):
                if isinstance(lst[i], str):
                    repair_field(f"lijst {name}[{i}]", lst, i)

    for label, a, b, steps, new in changes:
        print(f"GEREPAREERD {label}: {a} -> {b} tekens ({steps} stappen)\n    nieuwe tekst: {short(new)}")
    print(f"{len(changes)} strings gerepareerd.")

    if os.path.exists(NEW):
        with open(NEW, "r", encoding="utf-8-sig") as f:
            nieuw = json.load(f)
        bestaand = {e.get("id") for e in data["entries"]}
        for e in nieuw:
            if e.get("id") in bestaand:
                print(f"OVERGESLAGEN (id bestaat al): {e.get('id')}")
            else:
                data["entries"].append(e)
                bestaand.add(e.get("id"))
                print(f"TOEGEVOEGD: {e.get('id')} {e.get('datum')} {e.get('project')}")
    else:
        print("Geen nieuwe_regels.json gevonden; niets toegevoegd.")

    with open(DST, "w", encoding="utf-8", newline="") as f:  # UTF-8 zonder BOM
        json.dump(data, f, ensure_ascii=False, indent=2)
    print(f"Geschreven: {DST} ({os.path.getsize(DST)} bytes)")


if __name__ == "__main__":
    main()
