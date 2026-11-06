"""Validate the rule base in knowledge-base/rules and report overlaps between rules.

Usage: python3 tools/validate_kb.py
"""
import json
import re
import sys
from collections import defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
RULES_DIR = ROOT / "knowledge-base" / "rules"
SCHEMA = json.loads((ROOT / "knowledge-base" / "schema.json").read_text(encoding="utf-8"))

RULE_SCHEMA = SCHEMA["properties"]["rules"]["items"]
CATEGORIES = set(RULE_SCHEMA["properties"]["category"]["enum"])
SEVERITIES = set(RULE_SCHEMA["properties"]["severity"]["enum"])
CONFIDENCES = set(RULE_SCHEMA["properties"]["confidence"]["enum"])
REQUIRED = RULE_SCHEMA["required"]
ID_RE = re.compile(RULE_SCHEMA["properties"]["id"]["pattern"])


def check_rule(rule, where):
    errors = []
    for key in REQUIRED:
        if key not in rule:
            errors.append(f"{where}: missing '{key}'")
    if errors:
        return errors
    if not ID_RE.match(rule["id"]):
        errors.append(f"{where}: bad id")
    if rule["category"] not in CATEGORIES:
        errors.append(f"{where}: unknown category '{rule['category']}'")
    if rule["severity"] not in SEVERITIES:
        errors.append(f"{where}: unknown severity '{rule['severity']}'")
    if rule["confidence"] not in CONFIDENCES:
        errors.append(f"{where}: unknown confidence '{rule['confidence']}'")
    for field in ("title", "cause"):
        for lang in ("uk", "en"):
            if not rule[field].get(lang, "").strip():
                errors.append(f"{where}: empty {field}.{lang}")
    uk, en = rule["fix"].get("uk", []), rule["fix"].get("en", [])
    if not uk or len(uk) != len(en):
        errors.append(f"{where}: fix.uk/fix.en step count mismatch ({len(uk)} vs {len(en)})")
    match = rule["match"]
    if not match.get("any") and not match.get("regex") and "metric" not in rule:
        errors.append(f"{where}: no match patterns and no metric")
    for pattern in match.get("regex", []):
        try:
            re.compile(pattern)
        except re.error as e:
            errors.append(f"{where}: regex does not compile: {pattern!r} ({e})")
        if pattern.startswith("(?"):
            errors.append(f"{where}: inline regex flags are not portable to JavaScript: {pattern!r}")
    if not rule["sources"]:
        errors.append(f"{where}: no sources")
    return errors


def main():
    errors = []
    ids = {}
    by_substring = defaultdict(list)
    totals = defaultdict(int)

    for path in sorted(RULES_DIR.glob("*.json")):
        data = json.loads(path.read_text(encoding="utf-8"))
        for i, rule in enumerate(data["rules"]):
            where = f"{path.name}#{i} ({rule.get('id', '?')})"
            errors += check_rule(rule, where)
            rid = rule.get("id")
            if rid in ids:
                errors.append(f"{where}: duplicate id, also in {ids[rid]}")
            ids[rid] = path.name
            for s in {s.lower() for s in rule.get("match", {}).get("any", [])}:
                by_substring[s].append(rid)
            for other in rule.get("related", []):
                if other == rid:
                    errors.append(f"{where}: rule lists itself in 'related'")
            totals[path.name] += 1
            totals[f"confidence:{rule.get('confidence')}"] += 1
            totals[f"category:{rule.get('category')}"] += 1

    for key in sorted(totals):
        print(f"{key:40} {totals[key]}")
    print(f"{'TOTAL rules':40} {len(ids)}")

    overlaps = {s: r for s, r in by_substring.items() if len(r) > 1}
    if overlaps:
        print(f"\n{len(overlaps)} substrings are matched by more than one rule (expected between sources):")
        for s, r in sorted(overlaps.items()):
            print(f"  {s[:70]!r}: {', '.join(r)}")

    if errors:
        print(f"\n{len(errors)} ERRORS:")
        for e in errors:
            print("  " + e)
        sys.exit(1)
    print("\nOK")


if __name__ == "__main__":
    main()
