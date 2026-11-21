# builds assets/name-filter-words.txt for NsfwContentFilter from a SCOWL release
# run it like python scripts/build_name_filter_words.py path/to/scowl-2020.12.07
# get scowl from http://wordlist.aspell.net (the tarball has a final folder)
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
FILTER = os.path.join(ROOT, "ScavPrototypeMP", "assets", "nsfw-server-filter.json")
DENYLIST = os.path.join(ROOT, "scripts", "name_filter_denylist.txt")
OUT = os.path.join(ROOT, "ScavPrototypeMP", "assets", "name-filter-words.txt")

# size 50 is the normal spellchecker size. bigger lists are full of junk words
LIST_FILE = re.compile(r"^(english|american|british)-(words|upper|proper-names|contractions)\.(10|20|35|40|50)$")

HEADER = """\
# words NsfwContentFilter treats as normal english. built by scripts/build_name_filter_words.py
# from SCOWL size 50 so dont edit by hand. lines with ! are from name_filter_denylist.txt and always block
# SCOWL license below
#
# Copyright 2000-2018 by Kevin Atkinson
#
# Permission to use, copy, modify, distribute and sell these word
# lists, the associated scripts, the output created from the scripts,
# and its documentation for any purpose is hereby granted without fee,
# provided that the above copyright notice appears in all copies and
# that both that copyright notice and this permission notice appear in
# supporting documentation. Kevin Atkinson makes no representations
# about the suitability of this array for any purpose. It is provided
# "as is" without express or implied warranty.
#
# full SCOWL copyright and credits are at http://wordlist.aspell.net/scowl-readme/
"""

LEET = str.maketrans("01!|34@5$7+", "oiiieaasstt")


# same thing as NsfwContentFilter.Normalize
def normalize(s):
    return "".join(c for c in s.lower().translate(LEET) if c.isalnum())


def read_list(path):
    with open(path, encoding="utf-8") as f:
        return [line.strip() for line in f if line.strip() and not line.startswith("#")]


def main():
    if len(sys.argv) != 2:
        sys.exit("usage: build_name_filter_words.py path/to/scowl")
    final = os.path.join(sys.argv[1], "final")

    with open(FILTER, encoding="utf-8") as f:
        blocked = {normalize(t) for t in json.load(f)["blocked"]} - {""}
    deny = {normalize(w) for w in read_list(DENYLIST)}

    words = set()
    for name in os.listdir(final):
        if not LIST_FILE.match(name):
            continue
        with open(os.path.join(final, name), encoding="latin-1") as f:
            for line in f:
                w = line.strip()
                # possessives and accented words just add noise
                if w.endswith("'s") or not re.fullmatch(r"[A-Za-z']+", w):
                    continue
                n = normalize(w)
                if len(n) >= 2 or n in ("a", "i"):
                    words.add(n)

    words -= blocked
    # only the ones scowl actually has. the rest of the denylist is already caught by the json terms
    denied = sorted(words & deny)
    words -= deny

    with open(OUT, "w", encoding="utf-8", newline="\n") as f:
        f.write(HEADER)
        for w in denied:
            f.write("!" + w + "\n")
        for w in sorted(words):
            f.write(w + "\n")
    print(f"wrote {len(words)} words and {len(denied)} denylist words to {OUT}")


if __name__ == "__main__":
    main()
