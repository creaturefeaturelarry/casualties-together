using System.Collections.Generic;
using UnityEngine;

// the live limb and face names of milky and dune, filled from the experiment art of a skin.
internal static class SpeciesAliases {
    // remote packages carry experiment filenames. the species on the wire picks the live names.
    internal static IReadOnlyDictionary<string, Sprite> WithAliases(
        IReadOnlyDictionary<string, Sprite> sprites, int species) {
        if (sprites == null || species < 1 || species > 2) {
            return sprites;
        }

        Dictionary<string, Sprite> copy = new Dictionary<string, Sprite>(sprites.Count + 32);
        foreach (KeyValuePair<string, Sprite> pair in sprites) {
            copy[pair.Key] = pair.Value;
        }

        Add(copy, species);
        return copy;
    }

    // game limbs look up by their stock sprite name. fill missing milky/dune keys from experiment art.
    internal static void Add(IDictionary<string, Sprite> sprites, int species) {
        if (sprites == null) {
            return;
        }

        if (species == 1) {
            Alias(sprites, "milkycrus", "experimentCrus");
            Alias(sprites, "mildownarm", "experimentDownArm");
            Alias(sprites, "milkydowntorso", "experimentDownTorso");
            Alias(sprites, "milkyfoot", "experimentFoot");
            Alias(sprites, "milkyhandb", "experimentHandB");
            Alias(sprites, "milkyhandf", "experimentHandF");
            Alias(sprites, "milkytail", "experimentTail");
            Alias(sprites, "milkythigh", "experimentThigh");
            Alias(sprites, "milkyuparm", "experimentUpArm");
            Alias(sprites, "milkytorso", "experimentUpTorso");
            Alias(sprites, "milkyeyeclosed", "experimentEyeClosed");
            Alias(sprites, "milkyeyegone", "experimentEyeGone");
            Alias(sprites, "milkyeyegonehealed", "experimentEyeGoneHealed");
            Alias(sprites, "milkyeyehalfclosed", "experimentEyeHalfClosed");
            Alias(sprites, "milkyeyehalfclosedback", "experimentEyeHalfClosedBack");
            Alias(sprites, "milkyeyehappy", "experimentEyeHappy");
            Alias(sprites, "milkyeyehappyback", "experimentEyeLookBack");
            Alias(sprites, "milkyeyelockedin", "experimentEyeLockedIn");
            Alias(sprites, "milkyeyelockedinback", "experimentEyeLockedInBack");
            Alias(sprites, "milkyeyeopen", "experimentEyeOpen");
            Alias(sprites, "milkyeyeopenback", "experimentEyeLookBack");
            Alias(sprites, "milkyeyepanic", "experimentEyePanic");
            Alias(sprites, "milkyeyesad", "experimentEyeSad");
            Alias(sprites, "milkyeyesadback", "experimentEyeSadBack");
            Alias(sprites, "milkyeyescared", "experimentEyeScared");
            Alias(sprites, "milkyeyescaredback", "experimentEyeScaredBack");
            Alias(sprites, "milkyheadback", "experimentHeadBack");
            Alias(sprites, "milkyheadbackmouthopen", "experimentHeadBackMouth");
            Alias(sprites, "milkyheadbackmouthhalfopen", "experimentHeadBackMouthMini");
            Alias(sprites, "milkydisfigure1", "experimentHeadDisfigured1");
            Alias(sprites, "milkydisfigure1healed", "experimentHeadDisfigured1Healed");
            Alias(sprites, "milkydisfigure2", "experimentHeadDisfigured2");
            Alias(sprites, "milkydisfigure2healed", "experimentHeadDisfigured2Healed");
            Alias(sprites, "milkheadmiddle", "headMiddle");
            Alias(sprites, "milkheadouter", "headoutline");
            return;
        }

        if (species != 2) {
            return;
        }

        Alias(sprites, "duneCrus", "experimentCrus");
        Alias(sprites, "duneDownArm", "experimentDownArm");
        Alias(sprites, "duneDownArmAlt", "experimentDownArm");
        Alias(sprites, "duneDownTorso", "experimentDownTorso");
        Alias(sprites, "duneFoot", "experimentFoot");
        Alias(sprites, "duneHand", "experimentHandF");
        Alias(sprites, "duneHandAlt", "experimentHandB");
        Alias(sprites, "duneTail", "experimentTail");
        Alias(sprites, "duneThigh", "experimentThigh");
        Alias(sprites, "duneThighAlt", "experimentThigh");
        Alias(sprites, "duneUpArm", "experimentUpArm");
        Alias(sprites, "duneUpArmAlt", "experimentUpArm");
        Alias(sprites, "duneUpTorso", "experimentUpTorso");
        Alias(sprites, "duneEyesClosed", "experimentEyeClosed");
        Alias(sprites, "duneEyeGone", "experimentEyeGone");
        Alias(sprites, "duneEyeGoneHealed", "experimentEyeGoneHealed");
        Alias(sprites, "duneEyesHalfClosed", "experimentEyeHalfClosed");
        Alias(sprites, "duneEyeHappy", "experimentEyeHappy");
        Alias(sprites, "duneeyelockedin", "experimentEyeLockedIn");
        Alias(sprites, "duneEyes", "experimentEyeOpen");
        Alias(sprites, "duneEyePanic", "experimentEyePanic");
        Alias(sprites, "duneEyeSad", "experimentEyeSad");
        Alias(sprites, "duneEyeScared", "experimentEyeScared");
        Alias(sprites, "duneHeadBack", "experimentHeadBack");
        Alias(sprites, "duneHeadMouth", "experimentHeadBackMouth");
        Alias(sprites, "duneHeadMouthHalf", "experimentHeadBackMouthMini");
        Alias(sprites, "duneDisfigured", "experimentHeadDisfigured1");
        Alias(sprites, "duneDisfiguredHealed", "experimentHeadDisfigured1Healed");
        Alias(sprites, "headMiddleDune", "headMiddle");
        Alias(sprites, "headOuterDune", "headoutline");
    }

    private static void Alias(IDictionary<string, Sprite> sprites, string liveName, string sourceName) {
        if (sprites == null || string.IsNullOrEmpty(liveName) || string.IsNullOrEmpty(sourceName)) {
            return;
        }

        if (sprites.ContainsKey(liveName)) {
            return;
        }

        if (!sprites.TryGetValue(sourceName, out Sprite sprite) || sprite == null) {
            return;
        }

        sprites[liveName] = sprite;
    }
}
