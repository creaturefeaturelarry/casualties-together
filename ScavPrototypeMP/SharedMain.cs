using CasualtiesTogetherUtils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Unity.Collections;
using LiteNetLib;
using UnityEngine;

namespace Together;






public class SharedMain : MonoBehaviour {

    public static float max_player_interaction_distance = 9f;
    public static float max_player_interaction_distance_sq => max_player_interaction_distance * max_player_interaction_distance;

    public static bool LocalWorldIsGenerating = false;
    public static bool LocalWorldIsGenerated = false;
    public static double LastWorldgenFinishTime = 0;

    static bool[,] CHUNKS_VISIBLE_TO_PLAYERS = new bool[16, 16];


    public static bool CheckIfDispersionPunishmentProtocolRuleIsActive() {
        return (Time.realtimeSinceStartupAsDouble - LastWorldgenFinishTime) > (Net.IsClient ? 10f : 20f) && ScavPlayer.AllLivingPlayers.Count() > 0
            && Multiplayer.rules.PlayerScatterDisallowed
            && Multiplayer.rules.ScatterMinGroupSize > 0;
    }

    //TODO Occasionally theres a nullreference !!!! when its called from MedicalSync.LateUpdate 
    public static bool CheckIfShouldActivateDispersionPunishmentProtocol(ScavPlayer plr, out float max_recorded_dist, out int count_plrs_in_range, out int required_count_plrs_in_range) {

        count_plrs_in_range = ScavPlayer.AllLivingPlayers.Count;

        required_count_plrs_in_range = 0;
        max_recorded_dist = 0;

        // theyre already dead, dont do anything
        if (!plr.IsAlive() || plr.levelPlayTime < 20) {
            return false;
        }

        // there is only one living player, dont check anything
        if (count_plrs_in_range <= 1) {
            return false;
        }
        var plrs = new List<ScavPlayer>(ScavPlayer.AllLivingPlayers);
        plrs.RemoveAll(x => (
                // || x.body.isCriticallyDying // commented cuz i wanna make them euthanize dying comrades
                x.levelPlayTime < 20
        ) );
        if (Net.IsServer) {
            plrs.RemoveAll(
                x => (
                    !x.ServerPlayerState.is_loaded_in
                )
            );
        }
        if (Multiplayer.rules.Teams) { // dont check those who are not in ur team
            plrs.RemoveAll( x => (
                    !x.IsInSameTeamAs(plr)
            ) );
        }
        if (plrs.Count <= 1) { // not everyone has loaded 
            return false;
        }

        //get required half count

        required_count_plrs_in_range = Math.Max(1, (int)Mathf.Ceil(((float)plrs.Count) * ((float)Multiplayer.rules.ScatterMinGroupSize) * 0.01f));


        var presqr = Util.MetersToTiles(Multiplayer.rules.ScatterPunishDistance);
        presqr *= presqr;
        var pos = plr.body.transform.position;
        count_plrs_in_range = 0;
        foreach (var x in ScavPlayer.AllLivingPlayers) {
            var d = KM.DistanceSqr(x.body.transform.position, pos);
            if (d < presqr) {
                count_plrs_in_range++;
                if (d > max_recorded_dist) {
                    max_recorded_dist = d;
                }
            }
        }
        max_recorded_dist = Mathf.Sqrt(max_recorded_dist);

        if (count_plrs_in_range >= required_count_plrs_in_range) {
            return false;
        }
        // the council has decided to kill them 
        return true;
    }



    public static void CreatePlayerCharacters() {
        if (Net.IsRunning) {
            if (ServerMain.CHARACTER_PREFAB != null) {
                Plugin.log.LogInfo("Creating other player characters!");

                foreach (var plr in ScavPlayer.ClientIdToPlayerDict.Values) {
                    try {
                        plr.CreateCharacter();
                    } catch (Exception e) {
                        log.error(e);    
                    }
                }

            } else {
                log.error("Tried to create characters before they exist! RESTART THE GAME!");
            }
        }
    }



    public static void ForceUpdatePlayerLists() {
        ScavPlayer.AllLivingPlayers.Clear();
        ScavPlayer.AllDeadPlayers.Clear();

        foreach (var plr in ScavPlayer.BodyToPlayerDict.Values) {
            if (plr == null) continue;
            if (plr.body == null) continue;
            if (plr.body.alive) {
                ScavPlayer.AllLivingPlayers.Add(plr);
            } else {
                ScavPlayer.AllDeadPlayers.Add(plr);
            }
        }

    }

    // Extended for multiplayer
    public static bool WorldInSimRange(Vector2 pos) {
        // offline still trusts the camera renderer. a kept throw has to count before that.
        if (SimKeep.Covers(pos))
            return true;
        bool stamped = false;
        if (!KrokV5Optimization.FixSimRange.Range(pos, ref stamped))
            return stamped;
        bool inSimRange = false;
        var blockpos = WorldGeneration.world.WorldToBlockPos(pos);
        var renderer = WorldGeneration.world.GetClosestChunkRenderer(blockpos);
        if (renderer != null) {
            inSimRange = renderer.enabled;
        }

        if (!Net.IsRunning) {
            return inSimRange;
        }

        if (Net.IsServer && !inSimRange) {
            return SharedMain.PositionIsInSimulatedChunk_BlockPos(blockpos);
        }

        return inSimRange;
    }

    public static bool PositionIsInSimulatedChunk(Vector2 pos) {
        return PositionIsInSimulatedChunk_BlockPos(WorldGeneration.world.WorldToBlockPos(pos));
    }
    public static bool PositionIsInSimulatedChunk_BlockPos(Vector2Int blockpos) {
        var chunkpos = WorldGeneration.world.BlockToChunkPos(blockpos);

        var ymax_m1 = CHUNKS_VISIBLE_TO_PLAYERS.GetLength(1) - 1;
        var xmax_m1 = CHUNKS_VISIBLE_TO_PLAYERS.GetLength(0) - 1;

        chunkpos.x = Mathf.Clamp(chunkpos.x, 0, xmax_m1);
        chunkpos.y = Mathf.Clamp(chunkpos.y, 0, ymax_m1);

        return CHUNKS_VISIBLE_TO_PLAYERS[chunkpos.x, chunkpos.y];
    }

    static int VISIBLE_CHUNK_RANGE_HALF = 2;
    static void _PlayerVisibleChunksAroundPos(Vector2 pos) {
        //OG: WorldGeneration.UpdateChunkVisibility V520  but kinda simplified

        float chunksize_inv = 1f / (float)WorldGeneration.CHUNKSIZE;
        int half_chunkWidth = (int)((float)WorldGeneration.world.chunkWidth * 0.5f);
        int half_chunkHeight = (int)((float)WorldGeneration.world.chunkHeight * 0.5f);

        var ymax_m1 = CHUNKS_VISIBLE_TO_PLAYERS.GetLength(1) - 1;
        var xmax_m1 = CHUNKS_VISIBLE_TO_PLAYERS.GetLength(0) - 1;

        int num1 = Mathf.Clamp((int)(pos.x * chunksize_inv) - VISIBLE_CHUNK_RANGE_HALF + half_chunkWidth  , 0, xmax_m1);
        int num2 = Mathf.Clamp((int)(pos.x * chunksize_inv) + VISIBLE_CHUNK_RANGE_HALF + 1 + half_chunkWidth  , 0, xmax_m1);
        int num3 = Mathf.Clamp((int)(pos.y * chunksize_inv) - VISIBLE_CHUNK_RANGE_HALF + half_chunkHeight , 0, ymax_m1);
        int num4 = Mathf.Clamp((int)(pos.y * chunksize_inv) + VISIBLE_CHUNK_RANGE_HALF + 1 + half_chunkHeight , 0, ymax_m1);

        for (int y = num3; y <= num4; y++) {
            for (int x = num1; x <= num2; x++) {
                CHUNKS_VISIBLE_TO_PLAYERS[x, y] = true;
            }
        }

    }

    static void _UpdatePlayerVisibleChunks() {
        if (!KrokV5Optimization.FixSimRange.Rebuild())
            return;
        for (int y = 0; y < CHUNKS_VISIBLE_TO_PLAYERS.GetLength(1); y++) {
            for (int x = 0; x < CHUNKS_VISIBLE_TO_PLAYERS.GetLength(0); x++) {
                CHUNKS_VISIBLE_TO_PLAYERS[x, y] = false;
            }
        }

        if (!LocalWorldIsGenerated) {
            return;
        }
        _PlayerVisibleChunksAroundPos(WorldGeneration.world.mainCam.transform.position);
        if (!Net.IsRunning || Net.IsClient) {
            return;
        }
        
        foreach (var bp in ScavPlayer.BodyToPlayerDict) {
            _PlayerVisibleChunksAroundPos(bp.Value.pos);
        }
    }

    internal static float timer_SendPerformanceReport = 0;
    void Update() {
        LocalWorldIsGenerated = Util.IsWorldGenerated();
        LocalWorldIsGenerating = Util.IsGeneratingWorld();

        ForceUpdatePlayerLists();

        _UpdatePlayerVisibleChunks();


        if (!Net.IsConnected) return;

        timer_SendPerformanceReport += ClientMain.AdaptiveSyncTimerDelta;
        if (timer_SendPerformanceReport > 1) {
            timer_SendPerformanceReport = 0;
            UpdateSendPerformanceReport();
        }

    }

    static void UpdateSendPerformanceReport() {

        if (Net.IsClient) {

            var writer = Net.CreateWriter(NetmsgId.CurrentPerformanceReport_fromclient);

            writer.Put(ServerMain.CURRENT_FPS);
            writer.Put(ServerMain.CURRENT_TPS);
            
            //writer.Truncate();

            Net.Client_Send(DeliveryMethod.Unreliable, writer);

            // WRITER END;

        } else {

            var writer = Net.CreateWriter(NetmsgId.CurrentPerformanceReport);

            writer.Put(ServerMain.CURRENT_FPS);
            writer.Put(ServerMain.CURRENT_TPS);

            //writer.Truncate();

            Net.Server_SendToClients(DeliveryMethod.Unreliable, writer, ServerMain.AllClientIdsExceptHost);

            // WRITER END;
        }

    }

	[ClientReceiver((byte)NetmsgId.CurrentPerformanceReport, true)]
	static void ClientReceiver__CurrentPerformanceReport(knetid _, LiteNetLib.Utils.NetDataReader reader) {

		reader.Get(out ClientMain.SERVER_FPS);
		reader.Get(out ClientMain.SERVER_TPS);

	}


	[ServerReceiver((byte)NetmsgId.CurrentPerformanceReport_fromclient)]
	static void ServerReceiver__CurrentPerformanceReport_fromclient(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {

		if (ScavPlayer.TryGetPlayerFromClientId(clientId, out var plr)) {
			reader.Get(out plr.ServerPlayerState.fps);
			reader.Get(out plr.ServerPlayerState.tps);
		}

	}


}
















