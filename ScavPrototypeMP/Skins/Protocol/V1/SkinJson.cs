using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;

// V1: the skin editor's save file as pixel grids by part position (see SkinJsonNames).
internal sealed partial class SkinGrid {
    internal int Width;
    internal int Height;
    // top row first, RGBA, straight alpha.
    internal byte[] Rgba;
}

internal sealed partial class SkinJsonSet {
    internal readonly Dictionary<string, SkinGrid> Right = new Dictionary<string, SkinGrid>(StringComparer.Ordinal);
    internal int BadColors;
}

internal static partial class SkinJson {
    internal const int MaxJsonChars = 24 * 1024 * 1024;
    internal const int MaxSide = 512;
    private static readonly string[] SpeciesIds = { "expie", "milky", "dune" };

    internal static string SpeciesId(int species) {
        return species >= 0 && species < SpeciesIds.Length ? SpeciesIds[species] : null;
    }

    internal static bool TryParseV1(string json, int species, out SkinJsonSet set, out JObject root, out string error) {
        set = null;
        root = null;
        error = null;
        string id = SpeciesId(species);
        if (id == null) {
            error = "Unknown species " + species + ".";
            return false;
        }

        if (string.IsNullOrEmpty(json) || json.Length > MaxJsonChars) {
            error = "Skin file is empty or larger than " + MaxJsonChars / (1024 * 1024) + " MB.";
            return false;
        }

        try {
            root = JObject.Parse(json);
        } catch (Exception ex) {
            error = "Skin file is not valid JSON: " + ex.Message;
            return false;
        }

        SkinJsonSet result = new SkinJsonSet();
        ReadSection(root["skins"] as JObject, id, species, result.Right, result);
        if (result.Right.Count == 0) {
            error = "Skin file has no sprites for " + id + ".";
            return false;
        }

        set = result;
        return true;
    }

    private static void ReadSection(JObject section, string id, int species,
        Dictionary<string, SkinGrid> into, SkinJsonSet counters) {
        JArray parts = section == null ? null : section[id] as JArray;
        if (parts == null) {
            return;
        }

        string[][] names = SkinJsonNames.Species[species];
        for (int partAt = 0; partAt < parts.Count && partAt < names.Length; partAt++) {
            JArray variations = parts[partAt] as JArray;
            if (variations == null) {
                continue;
            }

            for (int variationAt = 0; variationAt < variations.Count && variationAt < names[partAt].Length; variationAt++) {
                string name = names[partAt][variationAt];
                if (name == null || into.ContainsKey(name)) {
                    continue;
                }

                SkinGrid grid = ReadGrid(variations[variationAt], counters);
                if (grid != null) {
                    into[name] = grid;
                }
            }
        }
    }

    // rows of "#rrggbb", "#rrggbbaa" or null, or the packed {"w","h","c"} form.
    private static SkinGrid ReadGrid(JToken token, SkinJsonSet counters) {
        if (token is JArray rows) {
            return ReadRows(rows, counters);
        }

        if (token is JObject packed) {
            return ReadPacked(packed, counters);
        }

        return null;
    }

    private static SkinGrid ReadRows(JArray rows, SkinJsonSet counters) {
        int height = rows.Count;
        if (height < 1 || height > MaxSide) {
            return null;
        }

        JArray first = rows[0] as JArray;
        int width = first == null ? 0 : first.Count;
        if (width < 1 || width > MaxSide) {
            return null;
        }

        SkinGrid grid = NewGrid(width, height);
        for (int y = 0; y < height; y++) {
            JArray row = rows[y] as JArray;
            if (row == null || row.Count != width) {
                return null;
            }

            for (int x = 0; x < width; x++) {
                SetPixel(grid, x, y, row[x], counters);
            }
        }

        return grid;
    }

    private static SkinGrid ReadPacked(JObject packed, SkinJsonSet counters) {
        int width = (int)(packed.Value<double?>("w") ?? 0);
        int height = (int)(packed.Value<double?>("h") ?? 0);
        if (width < 1 || height < 1 || width > MaxSide || height > MaxSide) {
            return null;
        }

        SkinGrid grid = NewGrid(width, height);
        JArray cells = packed["c"] as JArray;
        if (cells == null) {
            return grid;
        }

        foreach (JToken cell in cells) {
            JArray entry = cell as JArray;
            if (entry == null || entry.Count < 3) {
                continue;
            }

            int x = (int)(entry[0].Value<double?>() ?? -1);
            int y = (int)(entry[1].Value<double?>() ?? -1);
            if (x < 0 || y < 0 || x >= width || y >= height) {
                continue;
            }

            SetPixel(grid, x, y, entry[2], counters);
        }

        return grid;
    }

    private static SkinGrid NewGrid(int width, int height) {
        return new SkinGrid { Width = width, Height = height, Rgba = new byte[width * height * 4] };
    }

    private static void SetPixel(SkinGrid grid, int x, int y, JToken color, SkinJsonSet counters) {
        if (color == null || color.Type == JTokenType.Null) {
            return;
        }

        if (color.Type != JTokenType.String || !TryParseColor(color.Value<string>(), out byte r, out byte g, out byte b, out byte a)) {
            counters.BadColors++;
            return;
        }

        int at = (y * grid.Width + x) * 4;
        grid.Rgba[at] = r;
        grid.Rgba[at + 1] = g;
        grid.Rgba[at + 2] = b;
        grid.Rgba[at + 3] = a;
    }

    internal static bool TryParseColor(string text, out byte r, out byte g, out byte b, out byte a) {
        r = g = b = a = 0;
        if (text == null || (text.Length != 7 && text.Length != 9) || text[0] != '#') {
            return false;
        }

        if (!TryHex(text, 1, out r) || !TryHex(text, 3, out g) || !TryHex(text, 5, out b)) {
            return false;
        }

        a = 255;
        return text.Length == 7 || TryHex(text, 7, out a);
    }

    private static bool TryHex(string text, int at, out byte value) {
        return byte.TryParse(text.Substring(at, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }
}
