using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Together;

// dotnet run --project tests/NameFilter -p:SolutionDir=$PWD/ from the repo root. add -- --diff to list what it frees up
static class Checks {
    static int passed;

    // walks up from the build output til it finds the repo
    static string FindAssets() {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent) {
            string a = Path.Combine(d.FullName, "ScavPrototypeMP", "assets");
            if (File.Exists(Path.Combine(a, "nsfw-server-filter.json"))) return a;
        }
        throw new Exception("cant find ScavPrototypeMP/assets");
    }

    static void Check(bool value, string name) { if (!value) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }

    static int Main() {
        string assets = FindAssets();
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(assets, "nsfw-server-filter.json")));
        List<string> terms = json.RootElement.GetProperty("blocked").EnumerateArray().Select(e => e.GetString()).ToList();
        List<string> phrases = json.RootElement.GetProperty("blocked_phrases").EnumerateArray().Select(e => e.GetString()).ToList();
        List<string> lines = File.ReadAllLines(Path.Combine(assets, "name-filter-words.txt")).Where(l => l.Length > 0 && l[0] != '#').ToList();
        List<string> words = lines.Where(l => l[0] != '!').ToList();
        // same as NsfwContentFilter. the old filter never saw these so Old below only gets the json terms
        List<string> jsonTerms = terms.ToList();
        terms.AddRange(lines.Where(l => l[0] == '!').Select(l => l.Substring(1)));

        var load = System.Diagnostics.Stopwatch.StartNew();
        var m = new NameFilterMatcher(terms, phrases, words);
        Console.WriteLine($"{m.StrongCount} strong {m.WeakCount} weak {m.WordCount} words. load took {load.ElapsedMilliseconds} ms");

        // the old filter. everything stripped and then a plain substring search
        var oldTerms = jsonTerms.Concat(phrases).Select(NameFilterMatcher.Normalize).Where(t => t.Length > 0).ToArray();
        bool Old(string s) { string h = NameFilterMatcher.Normalize(s); return oldTerms.Any(t => h.Contains(t, StringComparison.Ordinal)); }

        string[] allowed = {
            "Masscas", "Massachusetts", "Sussex", "Essex", "therapist", "Cocktail Party", "Cockpit Crew",
            "Bass Fishing", "Class Act", "classact", "assassin main", "assassinmain", "Grape Juice", "Glass Hole",
            "The Assembly", "Peacock", "torpedo bay", "Spice Run", "Hancock", "Expedition Redacted", "Grasslands",
            "Scrapers", "Assault Team", "documentary", "cumulus", "Skyscraper", "Twinkle", "Montenegro",
        };
        foreach (string s in allowed) {
            Check(!m.ContainsBlocked(s), "allowed " + s);
        }

        string[] blocked = {
            "bitchassnigger", "bitch ass", "fuck", "f u c k", "fu ck", "sh1t", "b!tch", "assface", "sexy", "sexy lobby",
            "shitty", "motherfucker", "pornhub", "ass", "a s s", "ass69", "whorehouse", "jackass", "assclown", "dumbass", "asshat", "cockgoblin", "bitchier", "BITCH4SS", "fatass", "cuminmyass",
        };
        foreach (string s in blocked) {
            Check(m.ContainsBlocked(s), "blocked " + s);
        }

        // nothing the old filter let through gets blocked now
        foreach (string s in allowed.Concat(blocked).Concat(words)) {
            if (m.ContainsBlocked(s) && !Old(s)) throw new Exception("newly blocked " + s);
        }
        Check(true, "no new blocks");

        // every phrase and every strong term still blocks on its own
        foreach (string t in phrases.Concat(jsonTerms.Where(m.IsStrong))) {
            if (!m.ContainsBlocked(t)) throw new Exception("lost " + t);
        }
        Check(true, "strong terms and phrases still block");

        // the denylist words came out of the word list so they could stay blocked
        string deny = Path.Combine(assets, "../../scripts/name_filter_denylist.txt");
        foreach (string w in File.ReadAllLines(deny).Where(l => l.Length > 0 && l[0] != '#')) {
            if (Old(w) && !m.ContainsBlocked(w)) throw new Exception("denylist word got through " + w);
        }
        Check(true, "denylist words still block");

        if (args().Contains("--diff")) {
            var freed = words.Where(w => Old(w) && !m.ContainsBlocked(w)).ToList();
            Console.WriteLine($"{freed.Count} dictionary words the old filter blocked and this one allows");
            Console.WriteLine(string.Join(" ", freed));
        }

        // the browser runs this on every lobby so it has to stay cheap
        var bench = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 20000; i++) m.ContainsBlocked(i % 2 == 0 ? "Masscas bass fishing classact" : "bitchassnigger lobby");
        Console.WriteLine($"{bench.Elapsed.TotalMilliseconds * 1000 / 20000:F1} us per name");

        Console.WriteLine($"{passed} passed");
        return 0;
    }

    static string[] args() => Environment.GetCommandLineArgs();
}
