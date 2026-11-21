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

LIST_FILE = re.compile(r"^(english|american|british)-(words|upper|proper-names|contractions)\.(\d+)$")
# size 50 is the normal spellchecker size and decides which terms are weak. bigger lists are full of junk words
FULL_SIZE = 50
# only common words get shipped for splitting stuff like assface. size 20 is about 11k words
COMMON_SIZE = 20

HEADER = """\
# words NsfwContentFilter treats as normal english. built by scripts/build_name_filter_words.py from SCOWL
# so dont edit by hand. it has every size 50 word with a weak term in it plus the size 20 common words
# lines with ! are from name_filter_denylist.txt and always block. SCOWL license below
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
    common = set()
    for name in os.listdir(final):
        m = LIST_FILE.match(name)
        if not m or int(m.group(3)) > FULL_SIZE:
            continue
        size = int(m.group(3))
        with open(os.path.join(final, name), encoding="latin-1") as f:
            for line in f:
                w = line.strip()
                # possessives and accented words just add noise
                if w.endswith("'s") or not re.fullmatch(r"[A-Za-z']+", w):
                    continue
                n = normalize(w)
                if len(n) >= 2 or n in ("a", "i"):
                    words.add(n)
                    if size <= COMMON_SIZE:
                        common.add(n)

    words -= blocked
    # only the ones scowl actually has. the rest of the denylist is already caught by the json terms
    denied = sorted(words & deny)
    words -= deny

    # same weak test as NameFilterMatcher. every word with a weak term in it has to ship
    # or the matcher would call those terms strong and block class and bass again
    terms = blocked | set(denied)
    everything = "\n".join(words)
    weak = [t for t in terms if t in everything]
    keep = {w for w in words if any(t in w for t in weak)}
    full_count = len(words)
    words = keep | (common & words)

    with open(OUT, "w", encoding="utf-8", newline="\n") as f:
        f.write(HEADER)
        for w in denied:
            f.write("!" + w + "\n")
        for w in sorted(words):
            f.write(w + "\n")
    print(f"wrote {len(words)} of {full_count} words ({len(keep)} with a weak term) and {len(denied)} denylist words to {OUT}")


if __name__ == "__main__":
    main()
