using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Unity.VisualScripting;
using UnityEngine;


namespace Together;





[HarmonyPatch(typeof(PreRunScript), nameof(PreRunScript.StartRun))]
public static class PreRunScript_StartRun_MultiplayerPatch {
    static bool Prefix(PreRunScript __instance) {
        if (!LobbyHostTransfer.AllowRun(false)) return false;
        if (Net.IsRunning) {

            //TODO implement this
            //if (PlayerPrefs.GetInt("didbasiccourse") == 0) {
            //    tutorialWarning.SetActive(value: true);
            //    runSettingsScreen.SetActive(value: false);
            //    return false;
            //} 

#if FULLGAME
            // run-select: Start stays Start until host clicks it -> char-select Ready up
            CharacterSelectWait.OnStartOrLoadClicked(loadSave: false);
            return false;
#else
            if (Net.IsClient) {
                Multiplayer.DoMultiplayerStatusMessageLog(Lang.Get("client_cant_start_game"));
                return false;
            }
            SaveSystem.loadedRun = false;
            SavesystemPatch.DeleteMPSave();
            WorldGeneration.runSettings = __instance.runSettings;
            WorldMP.LoadVanillaGeneratedWorld(false);
            return false;
#endif
        } else {
            Multiplayer.DoMultiplayerStatusMessageLog(Multiplayer.CANT_PLAY_SP_MSG);
            return false;
        }
        return true;
    }
}

[HarmonyPatch(typeof(PreRunScript), nameof(PreRunScript.LoadRun))]
public static class PreRunScript_LoadRun_MultiplayerPatch {
    static bool Prefix(PreRunScript __instance) {
        if (!LobbyHostTransfer.AllowRun(true)) return false;
        if (Net.IsRunning) {
#if FULLGAME
            // continue: skip char select, load saved run immediately
            CharacterSelectWait.OnStartOrLoadClicked(loadSave: true);
            return false;
#else
            if (Net.IsClient) {
                Multiplayer.DoMultiplayerStatusMessageLog(Lang.Get("client_cant_start_game"));
                return false;
            }

            SaveSystem.loadedRun = true;
            WorldMP.LoadVanillaGeneratedWorld(true);
            return false;
#endif
        } else {
            Multiplayer.DoMultiplayerStatusMessageLog(Multiplayer.CANT_PLAY_SP_MSG);
            return false;
        }
        return true;
    }
}


[HarmonyPatch(typeof(PreRunScript), nameof(PreRunScript.StartTutorial))]
public static class PreRunScript_StartTutorial_MultiplayerPatch {
    static bool Prefix(PreRunScript __instance) {
        if (!LobbyHostTransfer.AllowRun(false)) return false;
        if (Net.IsRunning) {
            if (Net.IsClient) {
                Multiplayer.DoMultiplayerStatusMessageLog(Lang.Get("client_cant_start_game"));
                return false;
            }

#if FULLGAME
            // host picks a species, then Start loads the tutorial.
            CharacterSelectWait.HostBeginTutorialPick();
#else
            WorldMP.LoadVanillaTutorialWorld();
#endif
            return false;
        } else {
            //multiplayer.DoMultiplayerStatusMessageLog(Multiplayer.CANT_PLAY_SP_MSG);
            //return false;
        }
        return true;
    }
}

/*template
 
    [HarmonyPatch(typeof(), nameof(.))]
    public static class gfdgffffffffffffff {
        static bool Prefix(PreRunScript __instance, ref IEnumerator __result) {

            return true;
        }
    }
*/
























[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.GenerateBlockCircle))]
public static class WorldGenerationGenerateBlockCirclePatch {
    public static bool doing_autoUpdateChunk = false;

    public static bool Prefix(WorldGeneration __instance, Vector2 pos, int size, ushort block, float chance, float chanceEnd, bool autoUpdateChunk = false, bool force = false) {
        doing_autoUpdateChunk = autoUpdateChunk;
        return true;
    }
    public static void Postfix(WorldGeneration __instance, Vector2 pos, int size, ushort block, float chance, float chanceEnd, bool autoUpdateChunk = false, bool force = false) {
        doing_autoUpdateChunk = false;
    }
}


[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.DistributeMiniBarrels))]
public static class WorldGeneration_DistributeMiniBarrels_WorldgenPatch {
    public static bool Prefix(WorldGeneration __instance) {
        if (Multiplayer.IsNetworkActiveAndIsClient() || WorldMP.aaaaaaaaaaaaaaaaaaaaaaaaaaa != null) { // leave it up to server to generate this
            return false;
        }
        return true;
    }
}


[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.GenerateOres))]
static class WorldGeneration_GenerateOres_MultiplayerPatch {
    static bool Prefix() {
        // paints ore into worldBlocks and consumes Random. WorldGenerateStructures
        // (the cargo room included) runs immediately after. skipping it on clients
        // left their cargo in a different place than the host.
        return true;
    }
}



[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.Start))]
static class WorldGeneration_Start_MultiplayerPatch {
    static bool Prefix(WorldGeneration __instance) {
        if (Multiplayer.WorldSingletons != null) {
            foreach (var type in Multiplayer.WorldSingletons) {
                try {
                    __instance.GetOrAddComponent(type);
                } catch (Exception e) {
                    log.error(e);
                }
            }
        }
        // continue/LoadRun: apply mp_save biome before vanilla biomeDepth=debugStartDepth + TryLoadGame.
        // without this, MP TryLoadGame clears loadedRun and the first gen is always gravel layer 0.
        if (Net.IsRunning && Net.IsServer && !SavesystemPatch.PreserveSave
            && (SaveSystem.loadedRun || SavesystemPatch.PendingLoadMpSave)
            && SavesystemPatch.TryPeekMpWorldState(out var peek)) {
            SavesystemPatch.ApplyPeekedWorldState(__instance, peek);
            SaveSystem.loadedRun = true;
            Together.log.l($"Continue/load Start: debugStartDepth={peek.biomeDepth} midlevel={peek.midlevel} traveled={peek.totalTraveled}");
        }
        return true;
    }
}














[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.SetBlockNoUpdate))]
static class WorldGenerationSetBlockNoUpdatePatch {
    static bool Prefix(WorldGeneration __instance, Vector2Int pos, ushort block) {
        WorldGeneration_SetBlock_MultiplayerPatch.QueueBlockChange(__instance, pos, block, true);
        return true;
    }

    static void Postfix(WorldGeneration __instance, Vector2Int pos, ushort block) {
        if (block == 0)
            TrapSupport.OnBlockCleared(__instance, pos);
    }
}
[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.SetBlock), new[] { typeof(Vector2Int), typeof(ushort) })]
public static class WorldGeneration_SetBlock_MultiplayerPatch {
    public struct ChangedBlockInfo : INetSerializeByMemcpy {
        public Vector2Int16 pos;
        public ushort block;
        public ChangedBlockInfo(in Vector2Int16 p, in ushort b) {
            pos = p;
            block = b;
        }
        public BlockInfo GetBlockInfo() {
            return WorldGeneration.world.GetBlockInfo(block);
        }
        public static implicit operator BlockInfo(ChangedBlockInfo b) {
            return b.GetBlockInfo();
        }
    }

    static readonly int MaxChangedBlocksQueue = 96;
    public static readonly List<ChangedBlockInfo> changedBlocks = new(MaxChangedBlocksQueue);
    public static readonly List<ChangedBlockInfo> reliableChangedBlocks = new(64);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void QueueBlockChange(in WorldGeneration __instance, in Vector2Int pos, in ushort block, in bool IsNoUpdate) {
        if (!Multiplayer.IsNetworkActiveAndIsServer() || !SharedMain.LocalWorldIsGenerated) return;
        if (WorldGenerationGenerateBlockCirclePatch.doing_autoUpdateChunk) return;

        var b = new ChangedBlockInfo((Vector2Int16)pos, block);
        // a client who already received this chunk will not be sent it again. forget it
        // so walking back pulls the tiles placed while they were away.
        ForgetClientChunk(pos);
        // cooled lava has to arrive. the unreliable queue drops it once it fills.
        if (block == 37) {
            if (reliableChangedBlocks.Count < 256)
                reliableChangedBlocks.Add(b);
        } else if (changedBlocks.Count < MaxChangedBlocksQueue) {
            //if (changed_blocks.Count < 5 || (WorldGeneration.world.earthquakeIntensity > 0f && changed_blocks.Count < MaxChangedBlocksQueue)) {
            changedBlocks.Add(b);
        }
    }

    // the proximity resend skips a chunk marked known. placement while that client is
    // outside the ~100 block window used to stay on the host only.
    static void ForgetClientChunk(Vector2Int pos) {
        int cx = pos.x / WorldChunkSync.SYNCCHUNK_TILESIZE;
        int cy = pos.y / WorldChunkSync.SYNCCHUNK_TILESIZE;
        int dim = WorldChunkSync.SYNCCHUNK_DIMENSION;
        if ((uint)cx >= (uint)dim || (uint)cy >= (uint)dim)
            return;

        uint[,] hashes = WorldChunkSync.Server_ChunkHashes;
        if (hashes != null && cx < hashes.GetLength(0) && cy < hashes.GetLength(1))
            hashes[cx, cy] = 0;

        IReadOnlyList<ScavPlayer> players = ServerMain.AllPlayersExceptHost;
        if (players == null)
            return;
        for (int i = 0; i < players.Count; i++) {
            bool[,] known = players[i] != null && players[i].ServerPlayerState != null
                ? players[i].ServerPlayerState.known_chunks
                : null;
            if (known == null)
                continue;
            if (cx < known.GetLength(0) && cy < known.GetLength(1))
                known[cx, cy] = false;
        }
    }

    static NetDataWriter writer = Net.CreateWriter(NetmsgId.SetBlock);
    static NetDataWriter reliableWriter = Net.CreateWriter(NetmsgId.SetBlock);
    public static void FlushChangedBlocks() {
        if (!Multiplayer.IsNetworkActiveAndIsServer() || !SharedMain.LocalWorldIsGenerated || !WorldChunkSync._TILE_SYNC_ENABLED) {
            changedBlocks.Clear();
            reliableChangedBlocks.Clear();
            return;
        }
        if (changedBlocks.Count > 0) {
            int n = changedBlocks.Count;
            for (int i = 0; i < n; i++) {
                writer.Put(changedBlocks[i]);
            }
            // unreliable was dropped for anyone outside the live chunk window, and nothing sent it again.
            Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, clientIds: ServerMain.AllClientIdsExceptHost);
            writer.ResetKeepMsgId();
            changedBlocks.Clear();
        }
        if (reliableChangedBlocks.Count > 0) {
            int n = reliableChangedBlocks.Count;
            for (int i = 0; i < n; i++) {
                reliableWriter.Put(reliableChangedBlocks[i]);
            }
            Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, reliableWriter, clientIds: ServerMain.AllClientIdsExceptHost);
            reliableWriter.ResetKeepMsgId();
            reliableChangedBlocks.Clear();
        }
    }

    static bool Prefix(WorldGeneration __instance, Vector2Int pos, ushort block) {
        QueueBlockChange(__instance, pos, block, false);
        return true;
    }

    static void Postfix(WorldGeneration __instance, Vector2Int pos, ushort block) {
        if (block == 0)
            TrapSupport.OnBlockCleared(__instance, pos);
    }
}







[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.CreateExplosion))]
public static class WorldGeneration_CreateExplosion_MultiplayerPatch {

    public static event Action<ExplosionParams> OnExplosion;

    public static void ForceCreateExplosionEffect(ExplosionParams param) {
        _forcerun_tempbool = true;

        try {
            var newparam = param.CloneViaSerialization();
            newparam.structuralDamage = 0;

            // disable all damage 
            newparam.muscleDamage = new RangeF(0f, 0f);
            newparam.skinDamage = new RangeF(0f, 0f);
            newparam.skinDamageChance = 0.0f;
            newparam.boneBreakChance = 0.0f;
            newparam.dislocationChance = 0.0f;
            newparam.disfigureChance = 0.0f;
            newparam.bleedChance = 0.0f;
            newparam.bleedAmount = new RangeF(0f, 0f);
            newparam.structuralDamage = 0f;
            newparam.shrapnelChance = 0.0f;

            WorldGeneration.CreateExplosion(newparam);
        } finally {
            _forcerun_tempbool = false;
        }
    }

    public static ExplosionParams last_explosion_params = null;
    public static bool is_inside_CreateExplosion = false;
    static bool _forcerun_tempbool = false;
    static int hidRemote;
    static bool Prefix(ExplosionParams param) {
        is_inside_CreateExplosion = true;
        last_explosion_params = param;

        if (!Net.IsRunning) return true;
        if (_forcerun_tempbool) return true;
        if (Net.IsClient) return false;

        // world damage stays here. player damage is a claim, so remote bodies step out of the overlap.
        TrapClaim.HostBoom(param);
        hidRemote++;
        TrapClaim.HideRemoteBodies();
        return true;
    }
    static void Postfix(ExplosionParams param) {
        if (hidRemote > 0) {
            TrapClaim.ShowRemoteBodies();
            hidRemote--;
            if (hidRemote == 0)
                TrapClaim.PublishLocals(param);
        }
        is_inside_CreateExplosion = false;

        try {
            OnExplosion?.Invoke(param);
        } catch (Exception e) {

        }
    }
}





// this is called basically from everywhere to generate next layer
[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.RegenerateWorld))]
public static class RegenerateWorldPatch {

    public static void Prefix(WorldGeneration __instance, bool __0) {

        try {

            LobbyHostTransfer.ArmBoundary(__instance, __0);
            Con.UnNoclip();

            if (Net.IsRunning) {
                if (Net.IsServer) {

                    var writer = Net.CreateWriter(NetmsgId.CLIENT_Announce_RegenerateWorld);
                    writer.Put((ushort)(__instance.doPod ? 1 : 0));
                    Net.Server_SendToClientsVeryReliable(writer, ServerMain.AllClientIdsExceptHost);
                    // WRITER END;

                    Multiplayer.DoMultiplayerStatusMessageLog(Lang.Get("worldgen_announced_regen"));

                    if (!Multiplayer.rules.RespawnKeepInventory) {
                        // drop inventory of dead players for the items to get deleted 
                        foreach (var gggg in ScavPlayer.BodyToPlayerDict) {
                            if (!gggg.Key.alive) {
                                gggg.Value.Server_DropAllInventory();
                            }
                        }
                    }

                } else {
                    ClientMain.ServerIsGeneratingWorld = true;

                }
            }
            NetObjectRegistry.UnregisterAllObjectsButKeepInventory();


            SharedMain.LastWorldgenFinishTime = Time.realtimeSinceStartupAsDouble;

            foreach (var item in ScavPlayer.AllPlayers) {
                if (item.ServerPlayerState != null) {
                    item.ServerPlayerState.did_give_spawn_location = false;
                    item.ServerPlayerState.is_loaded_in = false;
                }

                item.ResetEntropy();
                item.levelPlayTime = 0;
            }


        } catch (Exception e) {
            log.error(e);
        }

    }

}

// create my own special ids for certain objects to sync it properly 
[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.GenerateEntityAtPos))]
public static class WorldGeneration_GenerateEntityAtPos_MultiplayerPatch {
    public static void Prefix(WorldGeneration __instance, Vector2 pos, GameObject basObj) {
        foreach (Transform ch in basObj.transform) {
            ScavMPBuildingSynchronizer.RegisterNonUniqueIdBg(ch, false);
        }
    }
}































// this could be avoided if the worldgen was seeded, but also its not required, but i think i need this to truly sync everything
[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.ApplyLayerModifiers))]
public static class WorldGeneration_ApplyLayerModifiers_MultiplayerPatch {
    public static bool sent_to_clients = false;

    public static bool Prefix(WorldGeneration __instance) {
        //if (WorldgenMP.current_instruction != null && WorldgenMP.current_instruction.apply_modifiers == false) {   return false;  }

        if (Net.IsRunning) {
            if (Net.IsClient
                || WorldMP.aaaaaaaaaaaaaaaaaaaaaaaaaaa != null // but also saveload does it on its own
            ) {
                return false; // its controlled by server
            }
            __instance.generatingWorld = true; // because some modifiers spawn shit, i dont want to trigger the server to send all the spawned entities lol
        }
        return true;
    }

    public static void Postfix(WorldGeneration __instance) {
        if (Net.IsRunning) {
            if (Net.IsServer) {
                // tell everyone what layer modifier has been applied
                foreach (LayerModifier layerModifier in LayerModifier.availableModifiers) {
                    if (layerModifier.active) {
                        log.l($"SERVER: Sending layer modifier: {nameof(layerModifier)}  index: {layerModifier.modifierIndex}");

#if FULLGAME
                        NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_Announce_LayerModifier);
                        writer.Put((ushort)layerModifier.modifierIndex);
                        writer.Put(Physics2D.gravity.y);
                        Net.Server_SendToClientsVeryReliable(writer, ServerMain.AllClientIdsExceptHost);
#else
                        Multiplayer.Server_SendRelayMessageToClients((byte)NetmsgId.CLIENT_Announce_LayerModifier, (knetid)layerModifier.modifierIndex, true, false);
#endif
                    }
                }
            }
        }
        sent_to_clients = true;

    }
}










[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.Update))]
static class WorldGeneration_Update_EarthquakeBlocksPatch {
    // stock disc is insideUnitCircle * Range(5, 30), so two centers collide inside 60.
    const float DiscReachSqr = 60f * 60f;
    static readonly List<Vector2> quake_centers = new();

    // stock only breaks tiles around the machine's own camera. host copies that roll onto everyone else.
    // overlapping discs keep one roll. the host camera already took its roll, so it wins any cluster it touches.
    static void Postfix(WorldGeneration __instance) {
        if (!Net.IsServer || __instance == null || __instance.earthquakeIntensity <= 0f)
            return;
        float dt = Time.deltaTime;
        if (dt <= 0f)
            return;
        float gate = 16f * __instance.earthquakeIntensity;
        quake_centers.Clear();
        var cam = PlayerCamera.main;
        if (cam != null && cam.body != null)
            quake_centers.Add(cam.body.transform.position);
        foreach (var plr in ScavPlayer.BodyToPlayerDict.Values) {
            if (plr == null || plr.IsLocal || plr.body == null || !plr.IsAlive())
                continue;
            Vector2 center = plr.body.transform.position;
            if (DiscHits(center))
                continue;
            quake_centers.Add(center);
            if (UnityEngine.Random.value / dt >= gate)
                continue;
            Vector2 at = center + UnityEngine.Random.insideUnitCircle * UnityEngine.Random.Range(5f, 30f);
            __instance.SetBlock(__instance.WorldToBlockPos(at), 0);
        }
    }

    static bool DiscHits(Vector2 center) {
        for (int i = 0; i < quake_centers.Count; i++) {
            if ((quake_centers[i] - center).sqrMagnitude <= DiscReachSqr)
                return true;
        }
        return false;
    }
}

// geothermal (depth 8): the top chunk is the drop-in. hot rock tiles, lava, and geysers there spawnkill.
[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.UpdateWorld))]
static class WorldGeneration_UpdateWorld_GeothermalSpawnRow_MultiplayerPatch {
    static void Prefix(WorldGeneration __instance) {
        if (__instance == null || __instance.biomeDepth != 8 || !__instance.generatingWorld) {
            return;
        }
        ScrubHotRock(__instance);
    }

    internal static bool InSpawnBand(Vector2 worldPos) {
        WorldGeneration world = WorldGeneration.world;
        if (world == null || world.biomeDepth != 8) {
            return false;
        }
        int chunk = WorldGeneration.CHUNKSIZE;
        if (chunk < 8) {
            chunk = 32;
        }
        return worldPos.y > (float)world.halfHeight - chunk;
    }

    static void ScrubHotRock(WorldGeneration world) {
        ushort[,] blocks = world.worldBlocks;
        if (blocks == null) {
            return;
        }
        int chunk = WorldGeneration.CHUNKSIZE;
        if (chunk < 8) {
            chunk = 32;
        }
        int w = blocks.GetLength(0);
        int h = blocks.GetLength(1);
        int y0 = h - chunk;
        if (y0 < 0) {
            y0 = 0;
        }
        for (int x = 0; x < w; x++) {
            for (int y = y0; y < h; y++) {
                if (blocks[x, y] == 36) {
                    blocks[x, y] = 37;
                }
            }
        }
    }
}

[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.PlaceLiquids))]
static class WorldGeneration_PlaceLiquids_GeothermalSpawnRow_MultiplayerPatch {
    static bool Prefix(WorldGeneration __instance, float perChunk, byte type, int maxFill) {
        if (__instance == null || __instance.biomeDepth != 8 || type != 6 || FluidManager.main == null) {
            return true;
        }
        int chunk = WorldGeneration.CHUNKSIZE;
        if (chunk < 8) {
            chunk = 32;
        }
        float num = (float)(__instance.chunkWidth * __instance.chunkHeight) * perChunk;
        float ceiling = (float)__instance.halfHeight - chunk;
        for (int i = 0; (float)i < num; i++) {
            Vector2 pos = new Vector2(
                UnityEngine.Random.Range(0L - (long)__instance.halfWidth, __instance.halfWidth),
                UnityEngine.Random.Range(0L - (long)__instance.halfHeight, ceiling));
            FluidManager.main.StartFill(__instance.WorldToBlockPos(pos), type, maxFill);
        }
        return false;
    }
}

[HarmonyPatch(typeof(GeyserScript), "Start")]
static class GeyserScript_Start_GeothermalSpawnRow_MultiplayerPatch {
    static void Postfix(GeyserScript __instance) {
        if (__instance == null) {
            return;
        }
        if (!WorldGeneration_UpdateWorld_GeothermalSpawnRow_MultiplayerPatch.InSpawnBand(__instance.transform.position)) {
            return;
        }
        UnityEngine.Object.Destroy(__instance.gameObject);
    }
}

[HarmonyPatch(typeof(WorldGeneration), "GeothermalLavaCool")]
static class WorldGeneration_GeothermalLavaCool_AllPlayers_MultiplayerPatch {
    // stock cools lava around the camera body only. host rolls that cool around a random living player.
    static bool Prefix(WorldGeneration __instance, ref IEnumerator __result) {
        if (!Net.IsRunning || !Net.IsServer)
            return true;
        __result = CoolLavaNearAllPlayers(__instance);
        return false;
    }

    static IEnumerator CoolLavaNearAllPlayers(WorldGeneration world) {
        while (world != null && !world.generatingWorld) {
            int passes = world.forceRain ? 5 : 1;
            for (int i = 0; i < passes; i++) {
                Body body = PickCoolBody();
                if (body == null)
                    continue;
                Vector2 pos = (Vector2)body.transform.position + UnityEngine.Random.insideUnitCircle * 16f;
                if (FluidManager.main != null && FluidManager.main.GetLiquid(pos) == 6) {
                    world.SetBlock(pos, 37);
                    FluidManager.main.SetLiquid(pos, 0);
                    if (WorldChunkSync.singleton != null)
                        WorldChunkSync.singleton.timer_TilemapFluidSync = 10f;
                }
            }
            yield return new WaitForSeconds(0.066f);
        }
    }

    static Body PickCoolBody() {
        List<ScavPlayer> living = ScavPlayer.AllLivingPlayers;
        if (living != null && living.Count > 0) {
            int start = UnityEngine.Random.Range(0, living.Count);
            for (int i = 0; i < living.Count; i++) {
                ScavPlayer plr = living[(start + i) % living.Count];
                if (plr?.body != null)
                    return plr.body;
            }
        }
        if (PlayerCamera.main == null)
            return null;
        return PlayerCamera.main.body;
    }
}

[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.DamageBlock), new Type[] { typeof(Vector2Int), typeof(float), typeof(bool), typeof(bool), typeof(bool) })]
public static class WorldGenerationDamageBlockAnnouncePatch {
    public static NetBody cur_damaging_netbody = null;

    public static void Postfix(WorldGeneration __instance, Vector2Int pos, float dmg, bool hitSound = true, bool bonusMetal = false, bool ignoreLoot = false) {
        if (Net.IsRunning) {

#if DEBUG
            log.l($"DamageBlock {Time.time}  ");
#endif

            if (Net.IsServer) // is host or server
            {

                var blockDamage = __instance.GetBlockDamage(pos);

                if (blockDamage != null) {

                    var writer = Net.CreateWriter(NetmsgId.PlayerDamageBlockRelay);

                    if (cur_damaging_netbody != null) {
                        writer.Put(cur_damaging_netbody.netId);
                    } else {
                        writer.Put((knetid)0);
                    }
                    writer.Put(pos);
                    writer.Put(blockDamage.damage);


                    Net.Server_SendToClients(DeliveryMethod.Unreliable, writer, clientIds: ServerMain.AllClientIdsExceptHost);

                    // WRITER END;
                }

            } else if (Util.GetLocalBody() is var body && body != null) { // its local 

                // its currently called from  UseItemInHand > Attack > DamageBlock
                // and also this confirms that its me, the local client calling this.

                // old damageblock system was here, i completely redone the combat system so its not needed

            }
        }
    }

}








[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.FinishWorldGeneration))]
public static class WorldGeneration_FinishWorldGeneration_MultiplayerPatch {

    [HarmonyReversePatch]
    public static IEnumerator FinishWorldGeneration(object instance) {
        throw new NotImplementedException();
    }


    public static bool Prefix(WorldGeneration __instance, ref IEnumerator __result) {
        __result = WorldMP.Patched_FinishWorldGeneration();
        return false;
    }
}




[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.WorldPlacePlayer))]
public static class WorldGeneration_WorldPlacePlayer_MultiplayerPatch {

    [HarmonyReversePatch]
    public static IEnumerator WorldPlacePlayer(object instance) {
        throw new NotImplementedException();
    }

    public static bool Prefix(WorldGeneration __instance, ref IEnumerator __result) {
        __result = WorldMP.Patched_WorldPlacePlayer();
        return false;
    }
}



































#region WORLDGEN





[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.IncreaseDepthByLayer))]
static class WorldGeneration_IncreaseDepthByLayer_MultiplayerPatch {

    // stock pays 80 intelligence to the camera body only. a skip calls this twice
    static void Postfix() {
        if (!Net.IsServer) return;
        foreach (var body in ScavPlayer.BodyToPlayerDict.Keys) {
            if (body == null || body.skills == null)
                continue;
            if (!body.IsBodyLocal()) // it was already given to local player
                SkillSync.HostGrant(body, 2, 80f);
            if (body.TryGetNetBody(out NetBody nb))
                MedicalSync.Server_QueueSendCharacterHealth(nb, true);
        }
    }
}



[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.GenerateWorld))]
public static class WorldGeneration_GenerateWorld_MultiplayerPatch {

    [HarmonyReversePatch]
    public static IEnumerator GenerateWorld(object instance) {
        throw new NotImplementedException();
    }



    public static LastBeforeGenerationState firstworldgenparams;
    public static bool client_firstworldgenparams_are_used = true;
    static bool _specialPrefabsReady;

    // Resources.Load of a prefab we already rewrote can take the process down. log the id first.
    static GameObject LoadPrefab(string id) {
        log.l("[prefab] load " + id);
        try {
            GameObject go = Resources.Load<GameObject>(id);
            if (go == null) {
                log.warn("[prefab] missing " + id);
            }
            return go;
        } catch (Exception ex) {
            log.error("[prefab] load fail " + id + "\n" + ex);
            return null;
        }
    }

    static void PatchClimbable(string id, string newId) {
        GameObject go = LoadPrefab(id);
        if (go == null) {
            return;
        }
        try {
            TrackerClimbableEntity c = go.GetOrAddComponent<TrackerClimbableEntity>();
            BuildingEntity b = go.GetComponent<BuildingEntity>();
            if (b == null) {
                log.warn("[prefab] no building " + id);
                return;
            }
            if (string.IsNullOrEmpty(c.og_id)) {
                c.og_id = b.id;
            }
            if (newId != null) {
                b.id = newId;
            }
            log.l("[prefab] ok " + id);
        } catch (Exception ex) {
            log.error("[prefab] patch fail " + id + "\n" + ex);
        }
    }

    public static void InitializeSpecialResourcePrefabs() {
        if (_specialPrefabsReady) {
            log.l("[prefab] skip, already patched");
            return;
        }
        log.l("[prefab] begin");
        {
            GameObject ticks = LoadPrefab("CaveTicks");
            if (ticks != null) {
                try {
                    ticks.GetOrAddComponent<TrackerStaticCaveTicks>();
                    log.l("[prefab] ok CaveTicks");
                } catch (Exception ex) {
                    log.error("[prefab] patch fail CaveTicks\n" + ex);
                }
            }
        }
        {
            PatchClimbable("Special/sandvinerope", TrackerClimbableEntity.sandvine_new_id);
            PatchClimbable("Special/mushroomrope", TrackerClimbableEntity.mushroomrope_new_id);

#if FULLGAME
            PatchClimbable("Special/veinloop", TrackerClimbableEntity.veinloop_new_id);

            GameObject spicevine = LoadPrefab(TrackerClimbableEntity.spicevine_id);
            if (spicevine != null) {
                try {
                    TrackerClimbableEntity c = spicevine.GetOrAddComponent<TrackerClimbableEntity>();
                    BuildingEntity b = spicevine.GetComponent<BuildingEntity>();
                    if (b != null && string.IsNullOrEmpty(c.og_id)) {
                        c.og_id = b.id;
                    }
                    log.l("[prefab] ok " + TrackerClimbableEntity.spicevine_id);
                } catch (Exception ex) {
                    log.error("[prefab] patch fail " + TrackerClimbableEntity.spicevine_id + "\n" + ex);
                }
            }

            GameObject spicevineceiling = LoadPrefab(TrackerClimbableEntity.spicevineceiling_id);
            if (spicevineceiling != null) {
                try {
                    TrackerClimbableEntity c = spicevineceiling.GetOrAddComponent<TrackerClimbableEntity>();
                    BuildingEntity b = spicevineceiling.GetComponent<BuildingEntity>();
                    if (b != null) {
                        if (string.IsNullOrEmpty(c.og_id)) {
                            c.og_id = b.id;
                        }
                        // stock id is also "spicevine" - clients need the ceiling resource path
                        b.id = TrackerClimbableEntity.spicevineceiling_id;
                    }
                    log.l("[prefab] ok " + TrackerClimbableEntity.spicevineceiling_id);
                } catch (Exception ex) {
                    log.error("[prefab] patch fail " + TrackerClimbableEntity.spicevineceiling_id + "\n" + ex);
                }
            }
#endif

            // component only. these prefabs come from buildings and have no id rewrite.
            GameObject climbingropeextended = LoadPrefab("climbingropeextended");
            if (climbingropeextended != null) {
                try {
                    climbingropeextended.GetOrAddComponent<TrackerClimbableEntity>();
                    log.l("[prefab] ok climbingropeextended");
                } catch (Exception ex) {
                    log.error("[prefab] patch fail climbingropeextended\n" + ex);
                }
            }
            GameObject ladder = LoadPrefab("ladder");
            if (ladder != null) {
                try {
                    ladder.GetOrAddComponent<TrackerClimbableEntity>();
                    log.l("[prefab] ok ladder");
                } catch (Exception ex) {
                    log.error("[prefab] patch fail ladder\n" + ex);
                }
            }
            GameObject defibrack = LoadPrefab("Special/defibrack");
            if (defibrack != null) {
                try {
                    BuildingEntity rack = defibrack.GetComponent<BuildingEntity>();
                    if (rack == null) {
                        log.warn("[prefab] no building Special/defibrack");
                    } else {
                        rack.id = "Special/defibrack";
                        log.l("[prefab] ok Special/defibrack");
                    }
                } catch (Exception ex) {
                    log.error("[prefab] patch fail Special/defibrack\n" + ex);
                }
            }

        }
        {

            /*
    KMPSR_* ids for GenerateEntityAtPos structure floors (demo "background" / fullgame empty id)
             */

#if TESTING
            log.l("Force-registering backgrounds...");
#endif
            foreach (var item in new string[] {
                "Lifepod",
                "BioContainer",
                "Structures/SteelBridge",
                "Structures/CratePod",
                "Structures/MiniPod",
                "Structures/MedicalBuilding",
                "Structures/BrickLoot",
                "Structures/LongCorridor",
                "Structures/RestSpot",
                "Structures/BlueHeartCont",
                "Structures/CrystalSpawnPlatform",
                "Special/TutorialStructure",
                "Special/EndLab",
            }) {
                GameObject basObj = null;
                try {
                    basObj = LoadPrefab(item);
                    if (basObj == null) {
                        continue;
                    }
                    foreach (Transform ch in basObj.transform) {
                        ScavMPBuildingSynchronizer.RegisterNonUniqueIdBg(ch, false);
                    }
                } catch (Exception ex) { log.error($"{item} force-register fail {basObj}:\n" + ex.ToString()); }

            }

#if FULLGAME
            try {
                GameObject heartCont = LoadPrefab("Structures/BlueHeartCont");
                if (heartCont != null) {
                    BlueHeart[] hearts = heartCont.GetComponentsInChildren<BlueHeart>(true);
                    for (int i = 0; i < hearts.Length; i++) {
                        if (hearts[i] == null)
                            continue;
                        BuildingEntity heartBuild = hearts[i].GetComponent<BuildingEntity>();
                        if (heartBuild == null)
                            heartBuild = hearts[i].GetComponentInParent<BuildingEntity>();
                        if (heartBuild == null) {
                            heartBuild = hearts[i].gameObject.AddComponent<BuildingEntity>();
                            heartBuild.id = "KMPSR_BlueHeart";
                        } else if (string.IsNullOrEmpty(heartBuild.id) || heartBuild.id == "background") {
                            heartBuild.id = "KMPSR_BlueHeart";
                        }
                        if (heartBuild.id == "KMPSR_BlueHeart")
                            ScavMPBuildingSynchronizer.known_entities_with_nonunique_id[heartBuild.id] = heartBuild.gameObject;
                    }
                    log.l("[prefab] ok Structures/BlueHeartCont");
                }
            } catch (Exception ex) {
                log.error("[prefab] patch fail Structures/BlueHeartCont\n" + ex);
            }
#endif

        }
        {
            // patch heavy drill sound
            try {
                GameObject basObj = LoadPrefab("heavydrill");
                AudioSource audio = basObj != null ? basObj.GetComponent<AudioSource>() : null;

                if (audio != null) {
                    audio.spatialBlend = 1;
                    audio.volume *= 0.6f;

                    audio.maxDistance = 150;
                    audio.minDistance = 30;
                    audio.rolloffMode = AudioRolloffMode.Linear;
                    log.l("[prefab] ok heavydrill");
                }
            } catch (Exception ex) {
                log.error("[prefab] patch fail heavydrill\n" + ex);
            }

        }

        {
            // adding despawn component
            foreach (var item in NetObjectRegistry.lowpriority_objects_resourceids) {
                GameObject basObj = null;
                try {
                    basObj = LoadPrefab(item);
                    if (basObj == null) {
                        continue;
                    }
                    basObj.GetOrAddComponent<ItemDespawnerIfUntouched>();
                    log.l("[prefab] ok " + item);
                } catch (Exception ex) { log.error("[prefab] despawn fail " + item + " " + basObj + "\n" + ex); }
            }
        }

        _specialPrefabsReady = true;
        log.l("[prefab] done");
    }

    public static bool Prefix(WorldGeneration __instance, ref IEnumerator __result) {
        __result = WorldMP.Patched_GenerateWorld(__instance);
        return false;
    }


}





[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.SaveAndExit))]
public static class WorldGenerationSaveAndExitPatch {
    public static bool Prefix(WorldGeneration __instance) {
        if (Net.IsRunning) {
            PlayerCamera.main.body.forceWalk = false;

            if (Net.IsClient) {
                __instance.savePanel.SetActive(false);
                PlayerCamera.main.DoAlert(Lang.Get("wait_for_host"));
                return false;
            } else {

            }
        }
        return true;
    }
}



[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.ContinueRun))]
public static class WorldGeneration_ContinueRun_MultiplayerPatch {
    static bool Prefix(WorldGeneration __instance) {
        ServerMain._ded_server_switch_counter = -20;

        if (Net.IsRunning) {
            PlayerCamera.main.body.forceWalk = false;
#if FULLGAME
            // ascent advances only after every living player finishes the scroller
            if (FullgameWorldCompat.IsAscentLayer()) {
                __instance.savePanel.SetActive(false);
                return false;
            }
#endif
            if (Net.IsClient) {
                __instance.savePanel.SetActive(false);
                PlayerCamera.main.DoAlert(Lang.Get("wait_for_host"));
                return false;
            } else {

                if (!ServerMain.CheckIfEnoughPeopleAreAtLayerFinish()) {

                    PlayerCamera.main.DoAlert(Lang.Get("layerfinish_not_enough_plrs"));

                    __instance.savePanel.SetActive(value: false);

                    return false;
                }

                //OG: WorldGeneration.ContinueRun
                // here i just dont check how far the player is, and do the exact same thing that method does
                // because of ForNextLayerAllMustReachEnd, and anyway this menu only opens at the end or someone finishes

                var doingRegen = (bool)Traverse.Create(__instance).Field("doingRegen").GetValue();

                if (!doingRegen && !__instance.generatingWorld && __instance.worldExists) {

                    Multiplayer.DoMultiplayerStatusMessageLog(Lang.Get("host_continue_run_status"));
                    Chat.Server_ChatAnnouncement("<key:host_continue_run_announcement>");

                    // biomeDepth has not moved yet, so SaveGame's stored depth+1 is the next layer.
                    PlayerCheckpoint.WriteLayerTransition();

                    __instance.savePanel.SetActive(value: false);
                    __instance.StartCoroutine(__instance.RegenerateWorld());

                    return false;
                }
            }
            return false;
        }
        return true;
    }
}


// stock opens the exit panel from WorldGeneration.body's y, then closes it again when that
// one body is above the line. the headcount wants it open for whoever reached the bottom,
// so the two fights and the buttons flash.
static class LayerExitWhenHostDead {
    public static void Register() {
        ServerMain.ServerCustomCommandsDict["layer_continue"] = (plr, cmd, args) => Accept(plr, false);
        ServerMain.ServerCustomCommandsDict["layer_save"] = (plr, cmd, args) => Accept(plr, true);
    }

    // true = this click was handled (host is dead). caller skips the wait-for-host alert.
    public static bool ClientAsk(WorldGeneration world, bool save) {
        if (!ServerMain.HostBodyIsDead() || world == null) {
            return false;
        }
#if FULLGAME
        if (FullgameWorldCompat.IsAscentLayer()) {
            world.savePanel.SetActive(false);
            return true;
        }
#endif
        if (!ServerMain.LocalPlayerAtLayerExit() || !ServerMain.CheckIfEnoughPeopleAreAtLayerFinish()) {
            PlayerCamera.main.DoAlert(Lang.Get("layerfinish_not_enough_plrs"));
            world.savePanel.SetActive(false);
            return true;
        }
        var writer = Multiplayer.CreateNamedWriter("CustomCommand");
        writer.Put(save ? "layer_save" : "layer_continue");
        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
        world.savePanel.SetActive(false);
        return true;
    }

    static void Accept(ScavPlayer plr, bool save) {
        // save and continue stay on the host's own panel. a client packet does not run them.
    }
}

[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.Update))]
static class WorldGeneration_Update_DeadHostExitPanel {
    static void Postfix(WorldGeneration __instance) {
        if (!Net.IsRunning || __instance == null || __instance.savePanel == null) {
            return;
        }
        // stock opens this on whoever is standing at the exit. clients never keep it.
        if (Net.IsClient) {
            if (__instance.savePanel.activeSelf) {
                __instance.savePanel.SetActive(false);
                if (PlayerCamera.main != null && PlayerCamera.main.body != null) {
                    PlayerCamera.main.body.forceWalk = false;
                    ScavPlayer plr = ScavPlayer.LocalPlayer;
                    if (plr != null && !__instance.doingRegen && !__instance.generatingWorld && __instance.worldExists && !plr.finishedLayer) {
                        plr.finishedLayer = true;
                        if (ServerMain.HostBodyIsDead() || ServerMain.GetNumberOfPlayersRequiredToFinishLayer() == 1) {
                            PlayerCamera.main.DoAlert(Lang.Get("layerfinish_wait_for_host"));
                        } else {
                            PlayerCamera.main.DoAlert(Lang.Get("layerfinish_wait_for_friends"));
                        }
                    }
                }
            }
            return;
        }
        ApplyHostExitPanel(__instance);
    }

    // last word for the frame. stock's y check must not win.
    internal static void ApplyHostExitPanel(WorldGeneration world) {
        if (world == null || world.savePanel == null) {
            return;
        }
        if (world.doingRegen || world.generatingWorld || !world.worldExists) {
            return;
        }
        bool open = ServerMain.CheckIfEnoughPeopleAreAtLayerFinish();
#if FULLGAME
        if (FullgameWorldCompat.IsAscentLayer()) {
            open = false;
        }
#endif
        if (world.savePanel.activeSelf != open) {
            world.savePanel.SetActive(open);
        }
        if (open && PlayerCamera.main != null && PlayerCamera.main.body != null) {
            PlayerCamera.main.body.forceWalk = false;
        }
    }
}




[HarmonyPatch(typeof(Heater), "OnWillRenderObject")]
public static class Heater_OnWillRenderObject_MultiplayerPatch {
    public static bool Prefix(Heater __instance) {
        if (Net.IsRunning) {
            // heating is completely moved to  Krokosha_Heater_MultiplayerReplacementComponent 
            return false;
        }
        return true;
    }
}





#endregion







[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.UpdateAmbientLight))]
public static class WorldGeneration_UpdateAmbientLight_MultiplayerPatch {
    //OG: WorldGeneration.UpdateAmbientLight V7.0.1
    public static float GetRunSettingsAmbientLightIntensity() {
        float intensity = 0f;
        switch (WorldMP.runSettings.GetValueOrDefault("ambientlight", 1)) {
            case 1:
            intensity = 0.12f;
            break;
            case 2:
            intensity = 0.4f;
            break;
        }
        return intensity;
    }


    static void Postfix(WorldGeneration __instance) {
        float intensity = 0;

        var worldState = ClientMain.ServerWorldState;
        if (worldState != null) {
            intensity = worldState.ambientLightIntensity.Value;
        } else {
            intensity = GetRunSettingsAmbientLightIntensity();
        }

        if (ConsoleScript.instance.fullBright || (!PlayerCamera_HandleScreenShaders_MultiplayerPatch.ShouldOverrideVisualsToDefault() && __instance.body.bothEyesGone)) {
            intensity = 0.7f;
        }
        __instance.ambientLight.intensity = intensity;
    }
}
