
using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Text;
using TMPro;
using UnityEngine;



namespace Together;






public static class FontUtils {
    public static string ReplaceMissingCharacters(TMP_FontAsset font, string input) {
        if (font == null || string.IsNullOrEmpty(input))
            return input;

        StringBuilder result = new StringBuilder(input.Length);

        foreach (char c in input) {
            if (font.HasCharacter(c,true))
                result.Append(c);
            else
                result.Append('?');
        }

        return result.ToString();
    }

    public static string ReplaceMissingCharacters(Font font, string input) {
        if (font == null || string.IsNullOrEmpty(input))
            return input;

        StringBuilder result = new StringBuilder(input.Length);

        // Request all characters at once (more efficient)
        font.RequestCharactersInTexture(input, font.fontSize, FontStyle.Normal);

        foreach (char c in input) {
            CharacterInfo info;
            if (font.GetCharacterInfo(c, out info, font.fontSize)) {
                result.Append(c);
            } else {
                result.Append('?');
            }
        }

        return result.ToString();
    }

    public static bool HasCharacter(Font font, char c) {
        if (font == null) return false;

        CharacterInfo info;

        // Request the character (important for dynamic fonts)
        font.RequestCharactersInTexture(c.ToString(), font.fontSize, FontStyle.Normal);

        return font.GetCharacterInfo(c, out info, font.fontSize);
    }
}


























