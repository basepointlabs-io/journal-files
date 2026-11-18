"""Makes the text rules work in every language of Revit, using Revit's own dialogs and string tables.

Usage: python3 tools/localize_rules.py [--check] [samples/revit-strings/2025]

For every rule that matches journal text it adds to the rule's "match":

  dialogs        the ids of the dialogs whose main instruction (the text Revit logs in the journal) the rule matches
                 in English. Revit logs the id under the text in every language, so the rule fires there too.
                 Ids already listed by hand are kept.
  translations   for messages Revit logs without an id (posted warnings and errors, plain message boxes): the same
                 message in German, French, Spanish, Italian and Portuguese, cut down to its longest part without
                 placeholders. Regenerated on every run.

Only what the rule already matches in English is carried over, so English journals are explained exactly as before.
Dialogs a rule matches only by title or body text (which the journal does not log) are listed, not added: adding
them would make the rule fire where it does not today, and that is a decision, not a translation.

Languages are limited to those the journal's Windows-1252 text can carry; for the others (Czech, Polish, Russian,
Japanese, Korean, Chinese) only dialog ids work.

Needs the dump made by tools/revit-strings.ps1 and tools/revit_dialogs.py (kept outside the repository).
--check changes nothing and prints what would change.
"""
import json
import re
import sys
from collections import defaultdict
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from revit_dialogs import ROOT, is_message, load  # noqa: E402

RULES_DIR = ROOT / "knowledge-base" / "rules"
LANGUAGES = ["de-DE", "fr-FR", "es-ES", "it-IT", "pt-BR"]
PLACEHOLDER_RE = re.compile(r"%\d+(?:![a-z]+!)?|%[A-Za-z]+%|%[sd]|\{\d+\}|\\n|\\t")
BUTTON_ID_RE = re.compile(r"_\d{3,4}$")       # TaskDialog_X_1001 is a button of TaskDialog_X
MAIN_INSTRUCTION = 1                          # property number of the text the journal logs, in the English file
MAX_PHRASE = 80
MAX_STRINGS = 6                               # a phrase that matches more messages than this is too general to translate
# Rules whose patterns hit Revit strings that are never journal messages (names of debug commands).
NO_TRANSLATIONS = {"forum-access-violation"}


def matcher(match):
    flags = re.IGNORECASE if match.get("ignore_case") else 0
    subs = [s.lower() if flags else s for s in match.get("any", []) if len(s) >= 10]
    regexes = [re.compile(p, flags) for p in match.get("regex", [])]

    def hit(text):
        hay = text.lower() if flags else text
        return any(s in hay for s in subs) or any(r.search(text) for r in regexes)
    return hit


def phrase(text):
    """The longest part of a message without placeholders: what a journal line of any model will contain."""
    parts = [p.strip() for p in PLACEHOLDER_RE.split(text)]
    best = max(parts, key=len, default="")
    if len(best) > MAX_PHRASE:
        best = best[:MAX_PHRASE].rsplit(" ", 1)[0]      # the start of a long message is enough to tell it apart
    return best if len(best) >= 12 else None


def load_strings(folder):
    table = defaultdict(dict)
    with open(Path(folder) / "strings.tsv", encoding="utf-8-sig") as f:
        for line in f:
            parts = line.rstrip("\n").split("\t", 3)
            if len(parts) == 4:
                table[(parts[1], parts[2])][parts[0]] = parts[3]
    return table


def main():
    args = [a for a in sys.argv[1:] if a != "--check"]
    check = "--check" in sys.argv
    folder = Path(args[0]) if args else ROOT / "samples" / "revit-strings" / "2025"
    dialogs = load(folder)
    strings = load_strings(folder)
    english = {}
    for dialog_id, languages in dialogs.items():
        texts = languages.get("en-US", [])
        english[dialog_id] = ([t for n, t in texts if n == MAIN_INSTRUCTION], [t for n, t in texts if n != MAIN_INSTRUCTION and is_message(t)])

    changed = by_id = translated = 0
    other_text = []
    for path in sorted(RULES_DIR.glob("*.json")):
        data = json.loads(path.read_text(encoding="utf-8"))
        for rule in data["rules"]:
            match = rule.get("match", {})
            if not (match.get("any") or match.get("regex")):
                continue
            hit = matcher(match)
            before = (list(match.get("dialogs", [])), list(match.get("translations", [])))

            ids = set(match.get("dialogs", []))
            for dialog_id, (main, rest) in english.items():
                if BUTTON_ID_RE.search(dialog_id):
                    continue
                if any(hit(t) for t in main):
                    ids.add(dialog_id)
                elif dialog_id not in ids and any(hit(t) for t in rest):
                    other_text.append((rule["id"], dialog_id))

            phrases = []
            if rule["id"] not in NO_TRANSLATIONS:
                found = [v for v in strings.values() if len(v.get("en-US", "")) >= 10 and hit(v["en-US"])]
                if len(found) <= MAX_STRINGS:
                    for v in found:
                        for language in LANGUAGES:
                            p = phrase(v.get(language, ""))
                            if p and p != phrase(v["en-US"]) and not hit(p) and p not in phrases:
                                phrases.append(p)

            if ids:
                match["dialogs"] = sorted(ids)
                by_id += 1
            if phrases:
                match["translations"] = phrases
                translated += 1
            elif "translations" in match:
                del match["translations"]
            if (match.get("dialogs", []), match.get("translations", [])) != before:
                changed += 1
                if check:
                    print(f"{rule['id']}: dialogs {match.get('dialogs', [])}, {len(phrases)} translated phrases")
        if not check:
            path.write_text(json.dumps(data, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")

    print(f"{by_id} rules keyed on dialog ids, {translated} with translated phrases, {changed} changed" + (" (check only)" if check else ""))
    if other_text:
        print("\nMatched only by a dialog's title or body, which the journal does not log (not added):")
        for rule_id, dialog_id in sorted(set(other_text)):
            print(f"  {rule_id}: {dialog_id}")


if __name__ == "__main__":
    main()
