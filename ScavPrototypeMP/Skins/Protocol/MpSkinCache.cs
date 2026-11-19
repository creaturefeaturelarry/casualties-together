using System;
using System.Collections.Generic;
using System.IO;

// file name is the sha-256. a full cache drops the oldest first.
internal sealed class MpSkinCache {
    internal const int MaxEntries = 32;

    private readonly string _root;
    private readonly long _maxBytes;

    internal MpSkinCache(string root, long maxBytes) {
        _root = root;
        _maxBytes = Math.Max(1024 * 1024, maxBytes);
    }

    internal string Root => _root;

    internal bool Contains(string hash) {
        return IsHash(hash) && File.Exists(PathFor(hash));
    }

    internal bool TryRead(string hash, out byte[] zip) {
        zip = null;
        if (!IsHash(hash)) {
            return false;
        }

        string path = PathFor(hash);
        if (!File.Exists(path)) {
            return false;
        }

        try {
            zip = File.ReadAllBytes(path);
            if (zip.Length == 0) {
                zip = null;
                return false;
            }
        } catch {
            return false;
        }

        try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch { }
        return true;
    }

    internal bool Store(string hash, byte[] zip) {
        if (!IsHash(hash) || zip == null || zip.Length == 0) {
            return false;
        }

        try {
            Directory.CreateDirectory(_root);
            string path = PathFor(hash);
            string temp = path + ".tmp";
            File.WriteAllBytes(temp, zip);
            if (File.Exists(path)) {
                File.Delete(path);
            }

            File.Move(temp, path);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            Prune();
            return true;
        } catch {
            return false;
        }
    }

    internal bool Remove(string hash) {
        if (!IsHash(hash)) {
            return false;
        }

        try {
            string path = PathFor(hash);
            if (!File.Exists(path)) {
                return false;
            }

            File.Delete(path);
            return true;
        } catch {
            return false;
        }
    }

    internal int Clear() {
        if (!Directory.Exists(_root)) {
            return 0;
        }

        int count = 0;
        foreach (string file in Directory.GetFiles(_root, "*.zip")) {
            try {
                File.Delete(file);
                count++;
            } catch { }
        }

        foreach (string file in Directory.GetFiles(_root, "*.tmp")) {
            try { File.Delete(file); } catch { }
        }

        return count;
    }

    internal long TotalBytes() {
        if (!Directory.Exists(_root)) {
            return 0;
        }

        long total = 0;
        foreach (string file in Directory.GetFiles(_root, "*.zip")) {
            try { total += new FileInfo(file).Length; } catch { }
        }
        return total;
    }

    internal int Count() {
        if (!Directory.Exists(_root)) {
            return 0;
        }

        return Directory.GetFiles(_root, "*.zip").Length;
    }

    internal void Prune() {
        if (!Directory.Exists(_root)) {
            return;
        }

        List<FileInfo> files = new List<FileInfo>();
        foreach (string path in Directory.GetFiles(_root, "*.zip")) {
            try { files.Add(new FileInfo(path)); } catch { }
        }

        files.Sort((a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));

        long total = 0;
        foreach (FileInfo file in files) {
            try { total += file.Length; } catch { }
        }

        int count = files.Count;
        foreach (FileInfo file in files) {
            if (count <= MaxEntries && total <= _maxBytes) {
                break;
            }

            try {
                long size = file.Length;
                file.Delete();
                total -= size;
                count--;
            } catch { }
        }
    }

    internal static bool IsHash(string hash) {
        if (string.IsNullOrEmpty(hash) || hash.Length != 64) {
            return false;
        }

        foreach (char value in hash) {
            bool digit = value >= '0' && value <= '9';
            bool lower = value >= 'a' && value <= 'f';
            bool upper = value >= 'A' && value <= 'F';
            if (!digit && !lower && !upper) {
                return false;
            }
        }

        return true;
    }

    private string PathFor(string hash) {
        return Path.Combine(_root, hash.ToLowerInvariant() + ".zip");
    }
}