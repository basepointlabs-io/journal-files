"""Prototype scanner: run the rule base and basic metrics over one Revit journal.

Usage: python3 tools/scan.py path/to/journal.NNNN.txt
"""
import json
import re
import sys
from collections import Counter, defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
RULES_DIR = ROOT / "knowledge-base" / "rules"

TS_RE = re.compile(r"^\s*'[CEH] (\d{2}-\S+-\d{4} \d{2}:\d{2}:\d{2}\.\d{3});")
RAM_STATS_RE = re.compile(r"RAM Statistics:\s+(\d+)\s*/\s*(\d+)\s+(\d+)=InUse\s+(\d+)=Peak")
BIG_GAP_RE = re.compile(r"'\s+([\d.]+)!+\s*\d+:!!!BIG_GAP (.+?)\s*$")
COMMAND_RE = re.compile(r'Jrn\.Command\s+"[^"]*"\s*,\s*"([^"]*)"')
DIALOG_ID_RE = re.compile(r"'Id : (\S+)")
RELEASE_RE = re.compile(r"^' Release: (\S+)")
BUILD_RE = re.compile(r"^' Build: (\S+)")
NORMAL_EXIT = ("finished recording journal file", "<-desktop ExitNativeInstance")


def load_rules():
    rules = []
    for path in sorted(RULES_DIR.glob("*.json")):
        for rule in json.loads(path.read_text())["rules"]:
            match = rule.get("match", {})
            flags = re.IGNORECASE if match.get("ignore_case") else 0
            subs = [s.lower() if flags else s for s in match.get("any", [])]
            regexes = [re.compile(p, flags) for p in match.get("regex", [])]
            excludes = match.get("exclude", [])
            if subs or regexes:
                rules.append((rule, bool(flags), subs, regexes, excludes))
    return rules


def scan(path):
    text = Path(path).read_bytes().decode("cp1252", errors="replace")
    lines = text.splitlines()
    rules = load_rules()

    info = {}
    last_ts = None
    first_ts = None
    hits = defaultdict(list)
    ram = []
    gaps = []
    commands = []
    dialogs = Counter()
    normal_exit = False

    for n, line in enumerate(lines, 1):
        if m := TS_RE.match(line):
            last_ts = m.group(1)
            first_ts = first_ts or last_ts
        if m := RELEASE_RE.match(line):
            info["release"] = m.group(1)
        if m := BUILD_RE.match(line):
            info["build"] = m.group(1)
        if any(s in line for s in NORMAL_EXIT):
            normal_exit = True
        if m := RAM_STATS_RE.search(line):
            avail, total, in_use, peak = map(int, m.groups())
            ram.append((avail, total, in_use, peak, last_ts, n))
        if m := BIG_GAP_RE.search(line):
            gaps.append((float(m.group(1)), m.group(2), last_ts, n))
        if m := COMMAND_RE.search(line):
            commands.append((last_ts, m.group(1), n))
        if m := DIALOG_ID_RE.search(line):
            dialogs[m.group(1)] += 1

        lower = None
        for rule, ci, subs, regexes, excludes in rules:
            hay = line
            if ci:
                lower = lower if lower is not None else line.lower()
                hay = lower
            if any(s in hay for s in subs) or any(r.search(line) for r in regexes):
                if any(x in line for x in excludes):
                    continue
                hits[rule["id"]].append((n, last_ts, line.strip()))

    return {
        "file": str(path),
        "lines": len(lines),
        "info": info,
        "first_ts": first_ts,
        "last_ts": last_ts,
        "normal_exit": normal_exit,
        "ram": ram,
        "gaps": sorted(gaps, reverse=True),
        "commands": commands,
        "dialogs": dialogs,
        "hits": hits,
        "rules": {r["id"]: r for r, *_ in rules},
    }


def report(res):
    print(f"# {res['file']}  ({res['lines']} lines)")
    print(f"Revit {res['info'].get('release')}  build {res['info'].get('build')}")
    print(f"Session: {res['first_ts']} -> {res['last_ts']}   normal exit: {res['normal_exit']}")
    if res["ram"]:
        low = min(res["ram"], key=lambda r: r[0])
        total = res["ram"][0][1]
        peak = max(r[3] for r in res["ram"])
        print(f"RAM: total {total} MB, Revit peak {peak} MB, lowest free {low[0]} MB at {low[4]} (line {low[5]})")
        print(f"     last sample: free {res['ram'][-1][0]} MB, Revit in use {res['ram'][-1][2]} MB")
    print("\nSlowest operations (BIG_GAP, seconds):")
    for secs, what, ts, n in res["gaps"][:10]:
        print(f"  {secs:8.1f}s  {what[:70]}  @ {ts} (line {n})")
    print("\nDialogs shown:")
    for d, c in res["dialogs"].most_common(15):
        print(f"  {c:4}x {d}")
    print("\nLast 12 commands:")
    for ts, cmd, n in res["commands"][-12:]:
        print(f"  {ts}  {cmd}  (line {n})")
    print("\nRule hits:")
    for rid, h in sorted(res["hits"].items(), key=lambda kv: -len(kv[1])):
        r = res["rules"][rid]
        print(f"  {len(h):5}x [{r['severity']}/{r['confidence']}] {rid}")
        print(f"         first: line {h[0][0]} @ {h[0][1]}: {h[0][2][:150]}")


if __name__ == "__main__":
    for p in sys.argv[1:]:
        report(scan(p))
        print("\n" + "=" * 100 + "\n")
