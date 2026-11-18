"""Coverage of the rule base: which signals in real journals no rule explains yet.

Usage: python3 tools/coverage.py [folder ...] [--out report.md]
       (default folder: samples/, default output: samples/coverage.md; both are gitignored)

A "signal" is something Revit writes when things go wrong, reduced to its type so that the same thing counts once
however often and with whatever ids it appears:
  dialog     a TaskDialog, by its Id (the same in every language) or, when Revit logs no Id, by its text
  failure    a warning or error Revit posted ("Error posted:", "Error dialog data ...", "' N:< Error: ...")
  exception  an exception type (MemoryException, ArchiveException, System.ArgumentNullException ...)
  debug      a DBG_WARN / DBG_ERROR line, without its numbers and source location
  api        an API_ERROR / API_WARNING line from an add-in

For each signal the report says how often it was seen, in how many journals, in how many it was among the last lines of
a journal that did not shut down normally, and which rules fire on its lines. Signals no rule fires on come first,
those seen near an abnormal end on top: these are the rules worth writing next. Dialogs that a rule recognises only by
their English text, not by their Id, are listed separately: they are missed in other languages.

The report names journal files and quotes journal lines; keep it out of the repository.
"""
import re
import sys
from collections import defaultdict
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from scan import ROOT, load_rules  # noqa: E402

DIALOG_RE = re.compile(r"TaskDialog \"(.*)\"\s*$")
DIALOG_ID_RE = re.compile(r"^\s*'Id : (TaskDialog_\w+)")
EXCEPTION_RE = re.compile(r"\b((?:[A-Z][A-Za-z]+\.)*[A-Z][A-Za-z]*Exception)\b")
DEBUG_RE = re.compile(r"(DBG_(?:WARN|ERROR)): (.+?)(?:: line \d+ of |\s*$)")
POSTED_RE = re.compile(r"^\s*'\s*\d+:<\s+(Error|Warning): (.+?)(?: - \d+ times)?\s*$")
API_RE = re.compile(r"(API_(?:ERROR|WARNING)) \{\s*(.+?)\s*\}?\s*$")
NORMAL_END_RE = re.compile(r"Jrn\.Command .*ID_APP_EXIT|finished recording journal file", re.I)
TAIL = 400


def normalise(text):
    """Ids, sizes, paths and quoted names differ from journal to journal; the kind of message does not."""
    text = re.sub(r"[A-Za-z]:\\[^\s\"']+|\\\\[^\s\"']+", "<path>", text)
    text = re.sub(r"\"[^\"]{1,120}\"|'[^']{1,120}'", "<name>", text)
    text = re.sub(r"\b[0-9a-f]{8}-[0-9a-f-]{27}\b", "<guid>", text, flags=re.I)
    text = re.sub(r"\d+", "N", text)
    text = re.sub(r"^(?:Error|Warning): ", "", text.strip())                     # the same failure is logged with and without it
    text = re.sub(r"in \S+ assembly", "in <assembly> assembly", text)            # one signal, whichever add-in
    text = re.sub(r"(checkout the following N worksets:).*", r"\1 <names>", text)
    return text.strip()[:140]


def matching_rules(rules, line):
    hits = []
    lower = None
    for rule, ci, subs, regexes, excludes in rules:
        hay = line
        if ci:
            lower = lower if lower is not None else line.lower()
            hay = lower
        if (any(s in hay for s in subs) or any(r.search(line) for r in regexes)) and not any(x in line for x in excludes):
            hits.append(rule["id"])
    return hits


class Signal:
    def __init__(self):
        self.count = 0
        self.journals = set()
        self.near_end = set()
        self.rules = set()
        self.by_id = False       # a rule fired on the line that carries the dialog's Id
        self.texts = set()
        self.example = ""


def scan(path, rules, signals):
    lines = path.read_text(encoding="cp1252", errors="replace").splitlines()
    ended = any(NORMAL_END_RE.search(l) for l in lines[-TAIL:])
    name = f"{path.parent.name}/{path.name}"

    def see(kind, key, n, fired, text=None, by_id=False):
        s = signals[(kind, key)]
        s.count += 1
        s.journals.add(name)
        if not ended and n >= len(lines) - TAIL:
            s.near_end.add(name)
        s.rules.update(fired)
        s.by_id |= by_id
        if text:
            s.texts.add(text[:120])
        s.example = s.example or f"{name}:{n + 1}: {lines[n].strip()[:160]}"

    expect_posted = False
    for n, line in enumerate(lines):
        fired = None

        def rules_here():
            nonlocal fired
            if fired is None:
                fired = matching_rules(rules, line)
            return fired

        if expect_posted:
            expect_posted = False
            m = re.match(r"^\s*'\s*\d+:<\s+(.+?)\s*$", line)
            if m and "[Jrn." not in line:
                see("failure", normalise(m.group(1)), n, rules_here())

        if "TaskDialog \"" in line and (m := DIALOG_RE.search(line)):
            text = m.group(1)
            dialog_id, id_rules = None, []
            for k in range(n + 1, min(n + 4, len(lines))):
                if mid := DIALOG_ID_RE.match(lines[k]):
                    dialog_id = mid.group(1)
                    id_rules = matching_rules(rules, lines[k])
                    break
            see("dialog", dialog_id or "no Id: " + normalise(text), n, rules_here() + id_rules, text, by_id=bool(id_rules))
        elif "Error posted:" in line or "Warning posted:" in line or "Error dialog data for transaction" in line:
            expect_posted = True
        elif m := POSTED_RE.match(line):
            see("failure", normalise(m.group(2)), n, rules_here())

        if "DBG_" in line and (m := DEBUG_RE.search(line)):
            see("debug", f"{m.group(1)}: {normalise(m.group(2))}", n, rules_here())
        elif "API_" in line and (m := API_RE.search(line)):
            see("api", f"{m.group(1)}: {normalise(m.group(2))}", n, rules_here())
        if "xception" in line:
            for ex in set(EXCEPTION_RE.findall(line)):
                see("exception", ex, n, rules_here())
    return ended


def report(signals, journals, abnormal):
    out = [f"# Rule coverage\n", f"{journals} journals, {abnormal} without a normal shutdown, {len(signals)} distinct signals.\n"]
    open_ = [(k, s) for k, s in signals.items() if not s.rules]
    out.append(f"**{len(signals) - len(open_)} explained by a rule, {len(open_)} not.**\n")

    def table(rows, with_rules=False):
        head = "| kind | signal | seen | journals | near abnormal end |" + (" rules |" if with_rules else " example |")
        lines = [head, "|---|---|---:|---:|---:|---|"]
        for (kind, key), s in rows:
            last = ", ".join(sorted(s.rules)) if with_rules else f"`{s.example}`"
            lines.append(f"| {kind} | {key.replace('|', '/')} | {s.count} | {len(s.journals)} | {len(s.near_end)} | {last.replace('|', '/')} |")
        return lines

    order = lambda kv: (-len(kv[1].near_end), -len(kv[1].journals), -kv[1].count)
    out.append("## Not explained by any rule\n")
    out += table(sorted(open_, key=order))

    by_text = [(k, s) for k, s in signals.items() if k[0] == "dialog" and s.rules and not s.by_id and not k[1].startswith("no Id")]
    out.append("\n## Dialogs recognised only by their English text\n")
    out.append("A rule fires on the text; add the Id to its `match.any` so it also fires in other languages.\n")
    out += table(sorted(by_text, key=order), with_rules=True)

    no_id = [(k, s) for k, s in signals.items() if k[0] == "dialog" and k[1].startswith("no Id")]
    out.append("\n## Dialogs Revit logs without an Id\n")
    out.append("Only the text identifies them, and the text is translated.\n")
    out += table(sorted(no_id, key=order), with_rules=True)

    covered = [(k, s) for k, s in signals.items() if s.rules]
    out.append("\n## Explained\n")
    out += table(sorted(covered, key=order), with_rules=True)
    return "\n".join(out) + "\n"


def main():
    args = sys.argv[1:]
    out = ROOT / "samples" / "coverage.md"
    if "--out" in args:
        i = args.index("--out")
        out = Path(args[i + 1])
        del args[i:i + 2]
    folders = [Path(a) for a in args] or [ROOT / "samples"]
    # A crash copy (journal.NNNN.txt.copy.txt) counts when the journal itself is gone, as in the app.
    files = sorted({p for f in folders for p in f.rglob("journal.*.txt")
                    if re.fullmatch(r"journal\.\d+\.txt", p.name)
                    or (p.name.endswith(".txt.copy.txt") and not p.with_name(p.name[:-len(".copy.txt")]).exists())})
    rules = load_rules()
    signals = defaultdict(Signal)
    abnormal = sum(0 if scan(p, rules, signals) else 1 for p in files)
    out.write_text(report(signals, len(files), abnormal))
    open_ = sum(1 for s in signals.values() if not s.rules)
    print(f"{len(files)} journals, {len(signals)} signals, {open_} not explained -> {out}")


if __name__ == "__main__":
    main()
