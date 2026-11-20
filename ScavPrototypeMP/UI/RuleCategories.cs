using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Together;

// Layout only: which rules sit under which header of the Rules tab. A rule's name, default, range and
// the way it is synced are all defined on MultiplayerGameRules and are not touched here.
public static class RuleCategories {

    public sealed class Category {
        public readonly string id;
        public readonly string englishTitle;
        public readonly string[] ruleNames;

        public Category(string id, string englishTitle, params string[] ruleNames) {
            this.id = id;
            this.englishTitle = englishTitle;
            this.ruleNames = ruleNames;
        }

        public string Title => Lang.Get("rule_cat_" + id, true) ?? englishTitle;
    }

    // Order is the order on screen. Anything a future rule adds that is not listed here lands in OtherId.
    public static readonly Category[] All = {
        new Category("lobby", "Lobby and Joining",
            nameof(MultiplayerGameRules.PLAYER_COUNT_LIMIT), nameof(MultiplayerGameRules.LateJoinAllowed),
            nameof(MultiplayerGameRules.LateJoinSpectate), nameof(MultiplayerGameRules.SpawnOnPlayer),
            nameof(MultiplayerGameRules.UnchippedIsIndividual)),
        new Category("hud", "Nametags and HUD",
            nameof(MultiplayerGameRules.ShowPlayerDirections), nameof(MultiplayerGameRules.EnableNametags),
            nameof(MultiplayerGameRules.EnableStatusIcons), nameof(MultiplayerGameRules.UnchippedHideNametags),
            nameof(MultiplayerGameRules.TeammateListUsesRange)),
        new Category("text", "Text Chat",
            nameof(MultiplayerGameRules.EnableChatbox), nameof(MultiplayerGameRules.OnlyProximityChat),
            nameof(MultiplayerGameRules.UnchippedProximityChat), nameof(MultiplayerGameRules.SpeechImpairedChat),
            nameof(MultiplayerGameRules.HearingLossChat), nameof(MultiplayerGameRules.MindwipeDisablesChat),
            nameof(MultiplayerGameRules.DeadTextchat)),
        new Category("voice", "Voice and Audio",
            nameof(MultiplayerGameRules.VoicechatEnabled), nameof(MultiplayerGameRules.VoicechatQuality),
            nameof(MultiplayerGameRules.ProximityHearDistance), nameof(MultiplayerGameRules.SpeechImpairedVC),
            nameof(MultiplayerGameRules.HearingLossVC), nameof(MultiplayerGameRules.DeadVoicechat),
            nameof(MultiplayerGameRules.SleepingMute), nameof(MultiplayerGameRules.CharacterYapPublic),
            nameof(MultiplayerGameRules.EnableMP3Sync)),
        new Category("death", "Death and Revival",
            nameof(MultiplayerGameRules.Permadeath), nameof(MultiplayerGameRules.ReviveOnNextLevel),
            nameof(MultiplayerGameRules.ReviveFromTrader), nameof(MultiplayerGameRules.RespawnKeepInventory),
            nameof(MultiplayerGameRules.RespawnKeepSkills), nameof(MultiplayerGameRules.TrackPlayerDeaths),
            nameof(MultiplayerGameRules.LastStandAllowed), nameof(MultiplayerGameRules.SpectateWhileUnconscious),
            nameof(MultiplayerGameRules.AllowSpectatorFreecam)),
        new Category("coop", "Teamwork and Carrying",
            nameof(MultiplayerGameRules.AllowPush), nameof(MultiplayerGameRules.AlwaysAllowCarry),
            nameof(MultiplayerGameRules.PiggybackMaxStack), nameof(MultiplayerGameRules.PiggybackWeightMultiplier),
            nameof(MultiplayerGameRules.NoInventoryLock), nameof(MultiplayerGameRules.ConsentForFeeding),
            nameof(MultiplayerGameRules.MinigameMaxPlayers)),
        new Category("pvp", "PVP and Teams",
            nameof(MultiplayerGameRules.Teams), nameof(MultiplayerGameRules.PVP),
            nameof(MultiplayerGameRules.PVPCombatDismember), nameof(MultiplayerGameRules.PVPMoodDebuff),
            nameof(MultiplayerGameRules.PVPDamageMultiplier), nameof(MultiplayerGameRules.AmputateHealthyPlayers)),
        new Category("health", "Health and Recovery",
            nameof(MultiplayerGameRules.AdditionalBrainRegen), nameof(MultiplayerGameRules.AdditionalHealthRegen),
            nameof(MultiplayerGameRules.AdditionalHealthDecay), nameof(MultiplayerGameRules.SelfharmWitnessMoodDebuff)),
        new Category("world", "World and Progression",
            nameof(MultiplayerGameRules.LayerFinishPlrPercent), nameof(MultiplayerGameRules.LayerFinishKeepXOffset),
            nameof(MultiplayerGameRules.ScatterMinGroupSize), nameof(MultiplayerGameRules.ScatterPunishDistance),
            nameof(MultiplayerGameRules.StragglerRadlinePercent), nameof(MultiplayerGameRules.RadlineExtraDamage),
            nameof(MultiplayerGameRules.EnableSleep), nameof(MultiplayerGameRules.EnableTimeManipulation)),
        new Category("saving", "Saving",
            nameof(MultiplayerGameRules.SavePlayerState), nameof(MultiplayerGameRules.SavePlayerInventory),
            nameof(MultiplayerGameRules.SavePlayerPosition)),
        new Category("cheats", "Cheats and Debug",
            nameof(MultiplayerGameRules.sv_cheats), nameof(MultiplayerGameRules.AllowClientCheatCommands),
            nameof(MultiplayerGameRules.DebugWorldSaveSystem)),
        new Category("dedicated", "Dedicated Server",
            nameof(MultiplayerGameRules.AutoContinue), nameof(MultiplayerGameRules.AutoMinPlrsToStart),
            nameof(MultiplayerGameRules.AutoExitWhenAllDied), nameof(MultiplayerGameRules.AutoExitWhenAllLeft)),
    };

    public const string OtherId = "other";
    static readonly Category OtherCategory = new Category(OtherId, "Other");

    // The defaults the struct's field initializers give; what a rule's reset button restores.
    static readonly MultiplayerGameRules Defaults = new MultiplayerGameRules();

    static readonly Dictionary<string, bool> expanded = new();

    public static bool IsExpanded(Category category) => expanded.TryGetValue(category.id, out var open) && open;
    public static void SetExpanded(Category category, bool open) => expanded[category.id] = open;
    public static void SetAllExpanded(bool open) {
        foreach (var category in All.Append(OtherCategory)) {
            expanded[category.id] = open;
        }
    }

    public static object DefaultValue(FieldInfo field) => field.GetValue(Defaults);

    public static bool IsDefault(FieldInfo field, object current) {
        var fallback = DefaultValue(field);
        if (current is float currentFloat && fallback is float fallbackFloat) {
            return Math.Abs(currentFloat - fallbackFloat) < 0.05f; // the slider rounds to one decimal
        }
        return Equals(current, fallback);
    }

    // Every field appears in exactly one group: listed ones in their category, the rest under Other.
    public static List<(Category category, List<FieldInfo> fields)> Group(IEnumerable<FieldInfo> fields) {
        var byName = fields.ToDictionary(f => f.Name);
        var result = new List<(Category, List<FieldInfo>)>();
        var placed = new HashSet<string>();
        foreach (var category in All) {
            var members = new List<FieldInfo>();
            foreach (var name in category.ruleNames) {
                if (byName.TryGetValue(name, out var field) && placed.Add(name)) {
                    members.Add(field);
                }
            }
            if (members.Count > 0) {
                result.Add((category, members));
            }
        }
        var leftovers = fields.Where(f => !placed.Contains(f.Name)).ToList();
        if (leftovers.Count > 0) {
            result.Add((OtherCategory, leftovers));
        }
        return result;
    }
}
