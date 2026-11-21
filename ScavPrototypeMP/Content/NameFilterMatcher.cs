using System;
using System.Collections.Generic;
using System.Text;

namespace Together;

// the matching half of NsfwContentFilter. no unity in here so tests/NameFilter can build it
// strong terms show up in no english word so they still match anywhere like before
// weak terms like ass in class only count when the rest of the token is real words
internal sealed class NameFilterMatcher {
    // what can follow a blocked word and still be that word. the y in sexy or the s on a plural
    private static readonly string[] Endings = {
        "s", "es", "ed", "er", "ers", "est", "ier", "iest", "ies", "ing", "ings", "in",
        "y", "ty", "ie", "o", "os", "ish", "ist", "ists", "ism", "ity", "ily", "ly", "al", "ness",
    };

    private readonly HashSet<string> _words;
    private readonly HashSet<string> _blocked;
    private readonly string[] _strong;
    private readonly string[] _weak;
    private readonly string[] _phrases;
    private readonly int _maxPiece;

    public int StrongCount => _strong.Length;
    public int WeakCount => _weak.Length;
    public int WordCount => _words.Count;

    public NameFilterMatcher(IEnumerable<string> terms, IEnumerable<string> phrases, IEnumerable<string> words) {
        _blocked = NormalizeAll(terms);
        _words = NormalizeAll(words);
        _words.ExceptWith(_blocked);

        var phraseSet = NormalizeAll(phrases);
        _phrases = new string[phraseSet.Count];
        phraseSet.CopyTo(_phrases);

        // one big string so each term is a single IndexOf over the whole word list
        var sb = new StringBuilder();
        foreach (string w in _words) {
            sb.Append('\n').Append(w);
        }
        string all = sb.Append('\n').ToString();

        var strong = new List<string>();
        var weak = new List<string>();
        foreach (string t in _blocked) {
            (all.IndexOf(t, StringComparison.Ordinal) >= 0 ? weak : strong).Add(t);
        }
        _strong = strong.ToArray();
        _weak = weak.ToArray();

        int max = 1;
        foreach (string w in _words) max = Math.Max(max, w.Length);
        foreach (string t in _blocked) max = Math.Max(max, t.Length);
        _maxPiece = max;
    }

    public bool IsStrong(string term) => Array.IndexOf(_strong, Normalize(term)) >= 0;

    public bool ContainsBlocked(string text) {
        List<string> tokens = Tokenize(text);
        if (tokens.Count == 0) {
            return false;
        }

        // starts[i] is where token i begins inside joined
        var joinedSb = new StringBuilder();
        var starts = new int[tokens.Count];
        for (int i = 0; i < tokens.Count; i++) {
            starts[i] = joinedSb.Length;
            joinedSb.Append(tokens[i]);
        }
        string joined = joinedSb.ToString();

        // phrases are multi word on purpose so they still match across spaces
        foreach (string p in _phrases) {
            if (p.Length > 0 && joined.IndexOf(p, StringComparison.Ordinal) >= 0) {
                return true;
            }
        }

        foreach (string t in _strong) {
            for (int at = joined.IndexOf(t, StringComparison.Ordinal); at >= 0; at = joined.IndexOf(t, at + 1, StringComparison.Ordinal)) {
                if (StrongHitCounts(tokens, starts, at, at + t.Length)) {
                    return true;
                }
            }
        }

        foreach (string tok in tokens) {
            if (WeakTokenBlocked(tok)) {
                return true;
            }
        }
        return false;
    }

    // a hit inside one token always counts. a hit across tokens only counts if one of them isnt a real word
    // so a word split up with spaces gets caught but glass hole doesnt
    private bool StrongHitCounts(List<string> tokens, int[] starts, int from, int to) {
        int first = -1, last = -1;
        for (int i = 0; i < tokens.Count; i++) {
            int s = starts[i], e = s + tokens[i].Length;
            if (e > from && s < to) {
                if (first < 0) first = i;
                last = i;
            }
        }
        if (first == last) {
            return true;
        }
        for (int i = first; i <= last; i++) {
            if (!IsWordPiece(tokens[i]) && !IsDigits(tokens[i])) {
                return true;
            }
        }
        return false;
    }

    private bool WeakTokenBlocked(string tok) {
        if (_words.Contains(tok)) {
            return false;
        }
        bool hasWeak = false;
        foreach (string t in _weak) {
            if (tok.IndexOf(t, StringComparison.Ordinal) >= 0) {
                hasWeak = true;
                break;
            }
        }
        return hasWeak && BestSplitIsDirty(tok);
    }

    // splits tok into words and blocked terms. fewest pieces wins and a tie goes to the clean split
    // so assassinmain is assassin main and not ass ass in main
    private bool BestSplitIsDirty(string tok) {
        int n = tok.Length;
        var pieces = new int[n + 1];
        var clean = new bool[n + 1];
        for (int i = 1; i <= n; i++) pieces[i] = int.MaxValue;
        clean[0] = true;

        for (int j = 0; j < n; j++) {
            if (pieces[j] == int.MaxValue) continue;
            int maxLen = Math.Min(_maxPiece, n - j);
            for (int len = 1; len <= maxLen; len++) {
                string piece = tok.Substring(j, len);
                if (IsWordPiece(piece)) {
                    Relax(pieces, clean, j + len, pieces[j] + 1, clean[j]);
                } else if (_blocked.Contains(piece)) {
                    Relax(pieces, clean, j + len, pieces[j] + 1, false);
                    // blocked word plus an ending counts as one piece
                    foreach (string end in Endings) {
                        if (j + len + end.Length <= n && string.CompareOrdinal(tok, j + len, end, 0, end.Length) == 0) {
                            Relax(pieces, clean, j + len + end.Length, pieces[j] + 1, false);
                        }
                    }
                }
            }
        }
        return pieces[n] != int.MaxValue && !clean[n];
    }

    private static void Relax(int[] pieces, bool[] clean, int at, int count, bool isClean) {
        if (count < pieces[at]) {
            pieces[at] = count;
            clean[at] = isClean;
        } else if (count == pieces[at] && isClean) {
            clean[at] = true;
        }
    }

    // single letters are too easy to glue onto anything. a and i are the only real ones
    private bool IsWordPiece(string s) => (s.Length >= 2 || s == "a" || s == "i") && _words.Contains(s);

    private static bool IsDigits(string s) {
        foreach (char c in s) {
            if (!char.IsDigit(c)) return false;
        }
        return true;
    }

    // splits on spaces and punctuation and on letter/digit changes so ass69 is ass and 69
    // runs of single letters get glued back so s p a c e d letters still read as one word
    internal static List<string> Tokenize(string input) {
        var raw = new List<string>();
        if (string.IsNullOrEmpty(input)) {
            return raw;
        }
        var sb = new StringBuilder();
        bool digits = false;
        foreach (char c in input) {
            char m = MapLeet(char.ToLowerInvariant(c));
            if (!char.IsLetterOrDigit(m)) {
                Flush(raw, sb);
                continue;
            }
            bool d = char.IsDigit(m);
            if (sb.Length > 0 && d != digits) {
                Flush(raw, sb);
            }
            digits = d;
            sb.Append(m);
        }
        Flush(raw, sb);

        var tokens = new List<string>(raw.Count);
        for (int i = 0; i < raw.Count; i++) {
            if (raw[i].Length != 1) {
                tokens.Add(raw[i]);
                continue;
            }
            int j = i;
            var run = new StringBuilder();
            while (j < raw.Count && raw[j].Length == 1) {
                run.Append(raw[j]);
                j++;
            }
            tokens.Add(run.ToString());
            i = j - 1;
        }
        return tokens;
    }

    private static void Flush(List<string> into, StringBuilder sb) {
        if (sb.Length > 0) {
            into.Add(sb.ToString());
            sb.Clear();
        }
    }

    internal static string Normalize(string input) {
        if (string.IsNullOrEmpty(input)) {
            return "";
        }
        StringBuilder sb = new StringBuilder(input.Length);
        foreach (char c in input) {
            char mapped = MapLeet(char.ToLowerInvariant(c));
            if (char.IsLetterOrDigit(mapped)) {
                sb.Append(mapped);
            }
        }
        return sb.ToString();
    }

    private static HashSet<string> NormalizeAll(IEnumerable<string> raw) {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (raw == null) {
            return set;
        }
        foreach (string r in raw) {
            if (string.IsNullOrWhiteSpace(r)) continue;
            string n = Normalize(r);
            if (n.Length > 0) set.Add(n);
        }
        return set;
    }

    private static char MapLeet(char c) {
        switch (c) {
            case '0': return 'o';
            case '1': return 'i';
            case '!': return 'i';
            case '|': return 'i';
            case '3': return 'e';
            case '4': return 'a';
            case '@': return 'a';
            case '5': return 's';
            case '7': return 't';
            case '+': return 't';
            case '$': return 's';
            default: return c;
        }
    }
}
