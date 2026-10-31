using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteEntitySystem;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.UI;
using Random = UnityEngine.Random;

namespace Together;

public static class Constants {

    //public const string OG_DOWNLOAD_URL = "https://github.com/creaturefeaturelarry/casualties-together";
    public const string OG_DOWNLOAD_URL = "https://www.nexusmods.com/scavprototype/mods/67";
    public const string OG_MOD_DISCORD_URL = "https://discord.gg/7K6J6bhV8b";
    public const string OG_OFFICIAL_DISCORD_URL = "https://discord.gg/targetplanet";


    public static readonly ExplosionParams DefaultMineExplosionParams = new ExplosionParams { position = Vector3.up };

}
