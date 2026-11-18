"""Reads Revit's own dialog definitions: for every TaskDialog id, its texts in every language.

Usage: python3 tools/revit_dialogs.py [samples/revit-strings/2025]
       writes dialogs.tsv (id, language, property number, text) next to the baml folder

Input: the files tools/revit-strings.ps1 exports into <folder>/baml (taskfiles.<language>.baml, from UIFrameworkRes).
In these files a dialog is a run of text properties followed by the record that carries its id:

    0x24 <size> <property number, 2 bytes> <length> <text> <2 bytes>      title, main instruction, content, buttons ...
    0x19 <size> <length> TaskDialog_...                                   the id closes the dialog

The texts of a dialog come in the same order in every language (the property numbers differ), so the n-th text
of an id in one language is the translation of the n-th text in another.
Development tool; the output is Autodesk's text and stays out of the repository.
"""
import re
import sys
from collections import defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
ID_RE = re.compile(rb"\x19[\x05-\x7f]([\x0f-\x60])(TaskDialog_[A-Za-z0-9_]+)")


def varint(data, i):
    value = shift = 0
    while True:
        c = data[i]
        i += 1
        value |= (c & 0x7F) << shift
        shift += 7
        if c < 0x80:
            return value, i


def text_record(data, p):
    """The property at p as (property number, text, end), or None when p is not the start of one."""
    try:
        size, i = varint(data, p + 1)
        prop = data[i] | data[i + 1] << 8
        length, j = varint(data, i + 2)
        end = j + length + 2
        if size != end - (p + 1) or length == 0:
            return None
        return prop, data[j:j + length].decode("utf-8"), end
    except (IndexError, UnicodeDecodeError):
        return None


def dialogs(data):
    """id -> [(property number, text)], in file order."""
    result = {}
    texts = []
    p = 0
    while p < len(data):
        if data[p] == 0x24 and (rec := text_record(data, p)):
            texts.append(rec[:2])
            p = rec[2]
        elif data[p] == 0x19 and (m := ID_RE.match(data, p)) and len(m.group(2)) == m.group(1)[0]:
            result.setdefault(m.group(2).decode(), texts)
            texts = []
            p = m.end()
        else:
            p += 1
    return result


def is_message(text):
    """Sentences a user reads, not button captions, flags and links."""
    return len(text) >= 12 and " " in text and not text.startswith("<a ") and text not in ("True", "False")


def load(folder):
    """id -> language -> [(property number, text)] for every exported language."""
    table = defaultdict(dict)
    for path in sorted((Path(folder) / "baml").glob("taskfiles.*.baml")):
        language = path.name.split(".")[1]
        for dialog_id, texts in dialogs(path.read_bytes()).items():
            table[dialog_id][language] = texts
    return table


def main():
    folder = Path(sys.argv[1]) if len(sys.argv) > 1 else ROOT / "samples" / "revit-strings" / "2025"
    table = load(folder)
    rows = [f"{dialog_id}\t{language}\t{prop}\t{text.replace(chr(9), ' ').replace(chr(10), ' ').replace(chr(13), '')}"
            for dialog_id in sorted(table) for language in sorted(table[dialog_id]) for prop, text in table[dialog_id][language]]
    out = folder / "dialogs.tsv"
    out.write_text("\n".join(rows) + "\n", encoding="utf-8")
    languages = {l for d in table.values() for l in d}
    print(f"{len(table)} dialogs, {len(languages)} languages, {len(rows)} texts -> {out}")


if __name__ == "__main__":
    main()
