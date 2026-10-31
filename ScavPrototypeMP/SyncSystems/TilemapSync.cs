
using HarmonyLib;
using Together;
using CasualtiesTogetherUtils;
using LiteNetLib;
using LiteNetLib.Utils;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using UnityEngine;

namespace Together;










#region FLUID_OUTSIDE_OF_HOST_VIEW

[HarmonyPatch(typeof(FluidManager), nameof(FluidManager.SimulationRangeIndex))]
public static class FluidManager_SimulationRangeIndex_MultiplayerPatch {

    public static bool forcenext_avaiable = false;
    public static (RangeI, RangeI) forcenext ;

    static bool Prefix(ref (RangeI, RangeI) __result) {

        if (forcenext_avaiable) {
            forcenext_avaiable = false;
            __result = forcenext;
            return false;
        }

        return true;
    }
}


[HarmonyPatch(typeof(FluidManager), "FixedUpdate")]
public static class FluidManager_FixedUpdate_MultiplayerPatch {

    // original fluid sim code simulats 16 height regions up and moves up every frame. uh whatever. in short i need it the same
    static int simIndex = 0;

    static bool Prefix() {


        if (NetBody.all_instances.Count > 0 && Net.IsServer) {

            HashSet<Vector2UInt8> vecs = new();

            foreach (var body in NetBody.all_instances) {
                var pos = (Vector2)WorldGeneration.world.WorldToBlockPos(body.transform.position) / (float)WorldChunkSync.FLUIDSIMULATIONZONESIZE;

                vecs.Add((Vector2UInt8)(pos));
                var px = WorldChunkSync._CheckTheProxNum(pos.x);
                var py = WorldChunkSync._CheckTheProxNum(pos.y);
                vecs.Add((Vector2UInt8)(new Vector2(pos.x + px, pos.y)));
                vecs.Add((Vector2UInt8)(new Vector2(pos.x, pos.y + py)));
                vecs.Add((Vector2UInt8)(new Vector2(pos.x + px, pos.y + py)));
                
            }

            var max_fluidsimzones_dimension = WorldChunkSync.WORLDSIZE / WorldChunkSync.FLUIDSIMULATIONZONESIZE; // it becomes 8

            foreach (var fsimchunk in vecs) {
                if (fsimchunk.x > max_fluidsimzones_dimension-1) {
                    continue;
                }
                if (fsimchunk.y > max_fluidsimzones_dimension-1) {
                    continue;
                }

                var blockpos = new Vector2Int(fsimchunk.x * WorldChunkSync.FLUIDSIMULATIONZONESIZE, fsimchunk.y * WorldChunkSync.FLUIDSIMULATIONZONESIZE);
                RangeI item =  new RangeI(blockpos.x, blockpos.x + WorldChunkSync.FLUIDSIMULATIONZONESIZE);

                //OG: FluidManager.SimulationRangeIndex V5t4  but like i need to go +128 instead of under,  

                RangeI item2 = new RangeI(blockpos.y + simIndex, blockpos.y + 16 + simIndex );

                //and simIndex doesnt even reach 112 so i need to do the +16.  because otherwise it leaves out the y16 zone where no liquid moves lol
                if (simIndex + 16 >= 112) {
                    item2.max += 16;
                }

                //OG: FluidManager.SimulationRangeIndex V5t4
                if (item.min < 1) {
                item.min = 1;
                }
                if (item2.min < 1) {
                    item2.min = 1;
                }
                if (item.max < 1) {
                    item.max = 1;
                }
                if (item2.max < 1) {
                    item2.max = 1;
                }
                if (item.max > WorldGeneration.world.width - 2) {
                    item.max = (int)(WorldGeneration.world.width - 2);
                }
                if (item2.max > WorldGeneration.world.height - 2) {
                    item2.max = (int)(WorldGeneration.world.height - 2);
                }
                if (item.min > WorldGeneration.world.width - 2) {
                    item.min = (int)(WorldGeneration.world.width - 2);
                }
                if (item2.min > WorldGeneration.world.height - 2) {
                    item2.min = (int)(WorldGeneration.world.height - 2);
                }


                FluidManager_SimulationRangeIndex_MultiplayerPatch.forcenext_avaiable = true;
                FluidManager_SimulationRangeIndex_MultiplayerPatch.forcenext = (
                    item,
                    item2
                );


                FluidManager.main.SimulationStep();

            }

            //OG: FluidManager.SimulationRangeIndex V5t4
            simIndex += 16;
            if (simIndex >= 112) {

                simIndex = 0;
            }

            return false;
        }

        return true;
    }
}

#endregion




public class WorldChunkSync : CasualtiesTogetherSingleton {
    public static WorldChunkSync singleton;

    public static bool _TILE_SYNC_ENABLED = true;
    public static bool _FLUID_SYNC_ENABLED = true;

    public static int _fluid_chunksync_counter = 0;
    public static int _chunksync_counter = 0;

    const float TilemapSyncFrequency = 1.061102f;
    const float TilemapFluidSyncFrequency = 0.035067f;

    public float timer_TilemapSync = 0;
    public float timer_TilemapFluidSync = 0;

    public static WorldGeneration world => WorldGeneration.world;

    // this is 32x32 chunks grid
    public const int SYNCCHUNK_TILESIZE = 32;
    // the world is 16x16 chunks of tile size 64
    public const int SYNCCHUNK_DIMENSION = 16*2;// so it would be just double of the chunks.   and like, one real chunk contains 4 syncchunks ofc
    public const int SYNCREGION_SIZE = 8; // so client will pick a region of 8x8 chunks around them
    public const int HALF_SYNCREGION_SIZE = SYNCREGION_SIZE/2;


    public const int WORLDSIZE = 1024; // 16 * 64

    // max id rn is 35,  so it fits within 63, which is 6 bits
    public const int TILEID_BITSIZE = 6;
    // there are only 6 fluids. which fits in 7 which is 3 bits
    public const int FLUIDID_BITSIZE = 3;
    // but like do i have to?


    public const int FLUIDSIMULATIONZONESIZE = 128; // 64 + 64 // from SimulationRangeIndex
    public const int FLUIDCHUNKSIZE = 64;
    public const int HALF_FLUIDCHUNKSIZE = FLUIDCHUNKSIZE/2;
    public static int CHUNKSIZE => WorldGeneration.CHUNKSIZE;
    public static ushort[,] worldBlocks => WorldGeneration.world.worldBlocks;
    public static bool instantiatingWorld => WorldGeneration.world.instantiatingWorld;

    public static byte[,] fluid => FluidManager.main.fluid;

    //public static double[,] client_chunkIgnoreTimes = new double[SYNCCHUNK_TILESIZE, SYNCCHUNK_TILESIZE];
    public static uint[,] Server_ChunkHashes = new uint[SYNCCHUNK_TILESIZE, SYNCCHUNK_TILESIZE];


    public static readonly List<(
        knetid, // Player
        float, // distance
        Vector2UInt8 // syncchunk coordinate
    )> server_chunksync_queue = new();

    void Awake() {
        singleton = this;
    }

    void Start()  {

        Multiplayer.OnSceneChangeOrWorldStartGenerate += () => {

            Server_ChunkHashes = new uint[SYNCCHUNK_TILESIZE, SYNCCHUNK_TILESIZE];
        };


    }




    void LateUpdate() {
        if (!Net.IsRunning) return;

        if (Util.IsWorldGenerated()) {

            // announce SetTile

            WorldGeneration_SetBlock_MultiplayerPatch.FlushChangedBlocks();
            


            if (world.savePanel.activeSelf) {
                PlayerCamera.main.body.forceWalk = false;

                if (Multiplayer.rules.CheckIfLayerContinueIsDisabled()) {
                    world.savePanel.SetActive(value: false);
                } else {

                    var plr = ScavPlayer.LocalPlayer;

                    if (plr && !world.doingRegen && !world.generatingWorld && world.worldExists && !plr.finishedLayer) {

                        plr.finishedLayer = true;
                        if (ServerMain.GetNumberOfPlayersRequiredToFinishLayer() != 1) {
                            PlayerCamera.main.DoAlert(Lang.Get("layerfinish_wait_for_friends"));
                        } else {
                            PlayerCamera.main.DoAlert(Lang.Get("layerfinish_wait_for_host"));
                        }
                    }

                    if (Net.IsClient) {
                        world.savePanel.SetActive(value: false);
                    } else {
                        var newww = ServerMain.CheckIfEnoughPeopleAreAtLayerFinish();
                        if (newww != world.savePanel.activeSelf) {
                            world.savePanel.SetActive(newww);
                        }
                    }
                }


            }// tp player:@r @m


            if (Net.IsServer) {
                // send chunks the clients have requested

                for (int i = 0; i < Mathf.Max((int)(ScavPlayer.ClientIdToPlayerDict.Count / 2), 1); i++) {
                    if (server_chunksync_queue.Count > 0) {
                        var current_chunk = server_chunksync_queue.First();
                        server_chunksync_queue.RemoveAt(0);

                        if (ScavPlayer.TryGetPlayerFromClientId(current_chunk.Item1, out var plr)) {
                            Server_Sendchunk(current_chunk.Item3, current_chunk.Item1, false);
                        }
                    }
                }

                Server_ChunkHashes = KrokV5Optimization.FixChunkHashes.Rent(SYNCCHUNK_TILESIZE, SYNCCHUNK_TILESIZE);
            }


            // and run the loops

            timer_TilemapSync += ClientMain.AdaptiveSyncTimerDelta;
            timer_TilemapFluidSync += ClientMain.AdaptiveSyncTimerDelta;

            if (timer_TilemapSync > TilemapSyncFrequency) {
                timer_TilemapSync = 0;
                TilemapSyncUpdate();
            }
            if (timer_TilemapFluidSync > TilemapFluidSyncFrequency) {
                timer_TilemapFluidSync = 0;
                FluidTilemapSyncUpdate();
            }


        }
    }


    public static void Server_Sendchunk(Vector2UInt8 syncchunk_coordinate, knetid plrId, bool reliable = false) {
        if (!ScavPlayer.TryGetPlayerFromClientId(plrId, out var plr)) {
            return;
        }
        Server_Sendchunk(syncchunk_coordinate, plr, reliable);    
    }
    public static void Server_Sendchunk(Vector2UInt8 syncchunk_coordinate, ScavPlayer plr, bool reliable = false) {
        if (!KrokV5Optimization.FixChunkBlock.AllowStock(syncchunk_coordinate, plr, reliable))
            return;

        var tiles = worldBlocks;

        var writer = Net.CreateWriter(NetmsgId.CLIENT_WorldTilemapChunk);

        writer.Put(syncchunk_coordinate.x);
        writer.Put(syncchunk_coordinate.y);

        var blockpos_x = (syncchunk_coordinate.x) * SYNCCHUNK_TILESIZE;
        var blockpos_y = (syncchunk_coordinate.y) * SYNCCHUNK_TILESIZE;

        using var outputstream = new MemoryStream();
        using (var gzipstream = new DeflateStream(outputstream, System.IO.Compression.CompressionLevel.Optimal)) {
            // write the entire chunk

            using var inputstream = new MemoryStream();
            for (int y = 0; y < SYNCCHUNK_TILESIZE; y++) {
                for (int x = 0; x < SYNCCHUNK_TILESIZE; x++) {
                    inputstream.WriteByte((byte)tiles[x + blockpos_x, y + blockpos_y]);
                }
            }
            var iarray = inputstream.ToArray();
            gzipstream.Write(iarray, 0, iarray.Length);
        }
        writer.PutBytesWithLength(outputstream.ToArray());

        Net.Server_SendToClients(reliable ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable, writer, plr.playerId);

        // WRITER END;

        plr.ServerPlayerState.known_chunks[syncchunk_coordinate.x, syncchunk_coordinate.y] = true;
    }


    public static NetDataWriter _Server_PackFluidChunk(in Vector2UInt8 syncchunk_coordinate) {
        if (KrokV5Optimization.FixChunkFluid.TryReplace(syncchunk_coordinate, out NetDataWriter packed))
            return packed;

        var blockpos_x = (syncchunk_coordinate.x) * SYNCCHUNK_TILESIZE;
        var blockpos_y = (syncchunk_coordinate.y) * SYNCCHUNK_TILESIZE;

        var writer = Net.CreateWriter(NetmsgId.CLIENT_WorldFluidTilemapChunk);

        writer.Put(syncchunk_coordinate.x);
        writer.Put(syncchunk_coordinate.y);

        var f = fluid;

        using var outputstream = new MemoryStream();
        using (var gzipstream = new DeflateStream(outputstream, System.IO.Compression.CompressionLevel.Optimal)) {
            // write the entire chunk

            using var inputstream = new MemoryStream();
            for (int y = 0; y < SYNCCHUNK_TILESIZE; y++) {
                for (int x = 0; x < SYNCCHUNK_TILESIZE; x++) {
                    inputstream.WriteByte((byte)f[x + blockpos_x, y + blockpos_y]);
                }
            }
            var iarray = inputstream.ToArray();
            gzipstream.Write(iarray, 0, iarray.Length);
        }
        writer.PutBytesWithLength(outputstream.ToArray());
        return writer;
    }
    public static void Server_SendFluidChunk(Vector2UInt8 syncchunk_coordinate, ScavPlayer plr, bool reliable = false) {
        var writer = _Server_PackFluidChunk(syncchunk_coordinate);
        Net.Server_SendToClients(reliable ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable, writer, plr.playerId);
        // WRITER END;
    }



    internal static void FluidTilemapSyncUpdate() {
        if (!Net.IsRunning) return;
        if (!_FLUID_SYNC_ENABLED) return;
        // generation swaps the fluid grid out. packing a chunk then throws.
        if (WorldGeneration.world == null || WorldGeneration.world.generatingWorld || FluidManager.main == null || fluid == null)
            return;


        if (Net.IsClient)
        {
        }
        else
        {
            //SERVER

            var plrblockpositions = new List<Vector2Int>();


            foreach (var plr in ScavPlayer.ClientIdToPlayerDict.Values) {
                if (plr.IsLocal || !plr.body || !plr.body.alive) {
                    continue;
                }

                var plredgepos = plr.pos - new Vector2 (FLUIDCHUNKSIZE*0.5f, FLUIDCHUNKSIZE * 0.5f) ;

                var bp = WorldGeneration.world.WorldToBlockPos(plredgepos);
                plrblockpositions.Add(bp);

                if (!plr.body.inWater && plr.body.liquidSlipTime <= 0 && plr.body.liquidRagdollBar >= 1 && plr.body.liquidDrinkTime <= 0) {
                    // synchronize the chunk under player, just so clients cant fall into schizo water, like waterbucket clutching except way harder and rarer
                    var bpunder = WorldGeneration.world.WorldToBlockPos(plredgepos + new Vector2(0,-(float)FLUIDCHUNKSIZE));
                    plrblockpositions.Add(bpunder);
                }
            }


            if (plrblockpositions.Count > 0)  {
                HashSet<Vector2UInt8> syncchunks = new();
                foreach (var chunk in plrblockpositions) {
                    var p = new Vector2Int(
                        (int)(chunk.x / SYNCCHUNK_TILESIZE),
                        (int)(chunk.y / SYNCCHUNK_TILESIZE)
                    ); 
                    for (int y = 0; y < 3; y++) {
                        for (int x = 0; x < 3; x++) {
                            var np = p + new Vector2Int(x, y);
                            np.Clamp(
                                new Vector2Int(0, 0),
                                new Vector2Int(SYNCCHUNK_DIMENSION-1, SYNCCHUNK_DIMENSION-1)
                            );
                            syncchunks.Add((Vector2UInt8)np);
                        }
                    }
                }


                if (_fluid_chunksync_counter >= syncchunks.Count) {
                    _fluid_chunksync_counter = 0;
                }

                var curcenterpos = syncchunks.ElementAt(_fluid_chunksync_counter);

                _fluid_chunksync_counter++;

                var writer = _Server_PackFluidChunk(curcenterpos);
                Net.Server_SendToClients( DeliveryMethod.Unreliable, writer, ServerMain.AllClientIdsExceptHost);

            }
        }
         
    }



    // this thing is ai generated (ai actually made it with a major bug lol) and im gona also add in a block counter to work 100% 
    public static uint HashRegion(in ushort[,] arr, int xmin, int xmax, int ymin, int ymax) {

        //if (xmin <= 0 || ymin <= 0 || xmax >= 1023 || ymax >= 1023) Plugin.log.LogWarning($"HashRegion: xmin:{xmin} xmax:{xmax} ymin:{ymin} ymax:{ymax}");


        // 64-bit FNV-1a constants
        const ulong fnvOffset = 1469598103934665603UL;
        const ulong fnvPrime = 1099511628211UL;

        ulong hash = fnvOffset;

        ushort counter = 0; // guranteed to stay max 1024,  so its 10 bits i think

        for (int y = ymin; y < ymax; y++) {
            for (int x = xmin; x < xmax; x++) {
                ushort v = arr[x, y];

                if (v != 0) {
                    counter++;
                }

                // Fold the 16-bit value into the hash — two bytes
                hash ^= (byte)v;
                hash *= fnvPrime;

                hash ^= (byte)(v >> 8);
                hash *= fnvPrime;
            }
        }

        var hash_thingy = (uint)(hash ^ (hash >> 32));


        return (hash_thingy << 10) | counter;
    }


    public static Vector2UInt8 WorldPosToSyncchunkCoordinate(Vector2 pos) {
        return BlockPosToSyncchunkCoordinate(WorldGeneration.world.WorldToBlockPos(pos));
    }
    public static Vector2UInt8 WorldPosToSyncchunkCoordinateSafe(Vector2 pos) {
        return BlockPosToSyncchunkCoordinateSafe(WorldGeneration.world.WorldToBlockPos(pos));
    }
    public static Vector2UInt8 BlockPosToSyncchunkCoordinate(Vector2Int blockpos) {
        return new Vector2UInt8((byte)(blockpos.x / SYNCCHUNK_TILESIZE), (byte)(blockpos.y / SYNCCHUNK_TILESIZE));
    }
    public static Vector2UInt8 BlockPosToSyncchunkCoordinateSafe(Vector2Int blockpos) {
        var cornerpos = new Vector2Int(
            (int)(blockpos.x / SYNCCHUNK_TILESIZE),
            (int)(blockpos.y / SYNCCHUNK_TILESIZE)
        );
        cornerpos.Clamp(
            new Vector2Int(0, 0),
            new Vector2Int(SYNCCHUNK_DIMENSION -1, SYNCCHUNK_DIMENSION -1)
        );
        return (Vector2UInt8)cornerpos;

    }





    internal static void TilemapSyncUpdate() {
        if (!Net.IsRunning) return;
        if (!_TILE_SYNC_ENABLED) return;

            

        if (Net.IsClient)  {
            // CLIENT


            // inventing the new tile sync:  tell server hashes of 32x32 chunks 

            var locplr_syncchunkpos = WorldPosToSyncchunkCoordinateSafe(
                PlayerCamera.main.body.alive ? PlayerCamera.main.body.transform.position : Camera.main.transform.position
            );

            // get top left corner of the region i, the client is interested in
            var cornerpos = new Vector2Int(
                locplr_syncchunkpos.x - HALF_SYNCREGION_SIZE,
                locplr_syncchunkpos.y - HALF_SYNCREGION_SIZE - 1
            );
            cornerpos.Clamp(
                new Vector2Int(0, 0),
                new Vector2Int(SYNCCHUNK_DIMENSION - SYNCREGION_SIZE, SYNCCHUNK_DIMENSION - SYNCREGION_SIZE)
            );


            var writer = Net.CreateWriter(NetmsgId.SERVER_HeyUhhMyChunksAreLikeThisBruh);

            writer.Put((byte)cornerpos.x);
            writer.Put((byte)cornerpos.y);

            for (int y = 0; y < SYNCREGION_SIZE; y++) {
                for (int x = 0; x < SYNCREGION_SIZE; x ++) {

                    var blockpos_x = (cornerpos.x + x) * SYNCCHUNK_TILESIZE;
                    var blockpos_y = (cornerpos.y + y) * SYNCCHUNK_TILESIZE;

                    var hash = HashRegion(worldBlocks,
                        blockpos_x,
                        blockpos_x + SYNCCHUNK_TILESIZE,
                        blockpos_y,
                        blockpos_y + SYNCCHUNK_TILESIZE
                    );

                    writer.Put(hash);

                }
            }


            Net.Client_Send(DeliveryMethod.Unreliable, writer);

            // WRITER END;

        } else {
            //SERVER

            foreach (var plr in ServerMain.AllPlayersExceptHost) {
                Server_CheckPlrPosChunk(plr);
            }
        }
        
    }

    public static void Server_CheckPlrPosChunk(ScavPlayer plr) {

        var pos = WorldPosToSyncchunkCoordinateSafe(plr.pos - new Vector2(SYNCCHUNK_TILESIZE*0.5f,0));

        for (int y = Mathf.Max(0,pos.y-2); y < Mathf.Min(pos.y + 1,WorldChunkSync.SYNCCHUNK_DIMENSION); y++) {
            for (int x = pos.x; x < Mathf.Min(pos.x + 2, WorldChunkSync.SYNCCHUNK_DIMENSION); x++) {
                //DebugHelp.OnNetEvent(WorldGeneration.world.BlockToWorldPos(new Vector2Int(x, y) * SYNCCHUNK_TILESIZE),"CHECKIUNG");
                if (!plr.ServerPlayerState.known_chunks[x, y]) {
                    var ggdfdfg = new Vector2UInt8(x, y);
                    Server_Sendchunk(ggdfdfg, plr, false);

                    Server_SendFluidChunk(ggdfdfg, plr, false);
                }
            }
        }

    }



    public static int _CheckTheProxNum(float n) {
        var d = n - Mathf.Floor(n);
        if (d < 0.4f)  {
            return -1;
        }  else if (d > 0.6f)  {
            return 1;
        }
        return 0;
    }


    byte[,] GetChunkData(Vector2Int chunk) {
        var realchunkpos = chunk * CHUNKSIZE;
        byte[,] chunkdata = new byte[CHUNKSIZE, CHUNKSIZE];
        for (int i = 0; i < CHUNKSIZE; i++)   {
            for (int j = 0; j < CHUNKSIZE; j++)    {
                chunkdata[i, j] = (byte)worldBlocks[i + realchunkpos.x, j + realchunkpos.y];
#if DEBUG // a warning for possible future
                if (chunkdata[i, j] > 254) { 
                    Plugin.log.LogWarning($"Block ids are reaching the limit! set byte to ushort !!!!!!!!!!!!!!!!!!  {chunkdata[i, j]} DELETEME ");
                }
#endif
            }
        }
        return chunkdata;
    }

    byte[,] GetFluidChunkData(Vector2Int lefttopcorner)  {
        byte[,] chunkdata = new byte[FLUIDCHUNKSIZE, FLUIDCHUNKSIZE];

        for (int i = 0; i <  FLUIDCHUNKSIZE; i++)  {
            for (int j = 0; j <  FLUIDCHUNKSIZE; j++)  {
                chunkdata[i, j] = fluid[i + lefttopcorner.x, j + lefttopcorner.y];
            }
        }
        return chunkdata;
    }




    [ClientReceiver((byte)NetmsgId.CLIENT_WorldFluidTilemapChunk)]
    static void ClientReceiver_WorldFluidTilemapChunk(knetid _, LiteNetLib.Utils.NetDataReader reader) {

        if (FluidManager.main == null || fluid == null) {
            return;
        }

        reader.Get(out byte cornersyncchunk_x);
        reader.Get(out byte cornersyncchunk_y);


        var blockorigin = new Vector2Int(
                    cornersyncchunk_x,
                    cornersyncchunk_y
        ) * SYNCCHUNK_TILESIZE;

        //if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(WorldGeneration.world.BlockToWorldPos(blockorigin), $"C: Received fluid chunk {cornersyncchunk_x},{cornersyncchunk_y}");

        var f = fluid;

        
        reader.Get(out byte[] compressed_chunkarray);

        var chunkarray = Util.DecompressDeflate(compressed_chunkarray);
        int i = 0;
        for (int y = 0; y < SYNCCHUNK_TILESIZE; y++) {
            for (int x = 0; x < SYNCCHUNK_TILESIZE; x++) {
                var value = chunkarray[i++];
                f[x + blockorigin.x, y + blockorigin.y] = value;
            }
        }
    }


    [ClientReceiver((byte)NetmsgId.CLIENT_WorldTilemapChunk)]
    static void ClientReceiver_WorldTilemapChunk(knetid _, LiteNetLib.Utils.NetDataReader reader) {

        if (world == null || instantiatingWorld) {
            return;
        }
        var tiles = worldBlocks;
        if (tiles == null) {
            return;
        }

        reader.Get(out byte cornersyncchunk_x);
        reader.Get(out byte cornersyncchunk_y);

        var blockorigin = new Vector2Int(
                    cornersyncchunk_x,
                    cornersyncchunk_y
        ) * SYNCCHUNK_TILESIZE;


        {
            reader.Get(out byte[] compressed_chunkarray);

            var chunkarray = Util.DecompressDeflate(compressed_chunkarray);
            int i = 0;
            for (int y = 0; y < SYNCCHUNK_TILESIZE; y++) {
                for (int x = 0; x < SYNCCHUNK_TILESIZE; x++) {
                    var value = chunkarray[i++];
                    tiles[x + blockorigin.x, y + blockorigin.y] = (ushort)value;
                }
            }
        }

        WorldGeneration.world.UpdateChunkClosest(blockorigin);
        
        if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(WorldGeneration.world.BlockToWorldPos(blockorigin), $"C: Received chunk data {cornersyncchunk_x},{cornersyncchunk_y}");

    }

    [ServerReceiver((byte)NetmsgId.SERVER_HeyUhhMyChunksAreLikeThisBruh)]
    static void Server_HeyUhhMyChunksAreLikeThisBruh(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        if (!Util.IsWorldGenerated()) {
            return;
        }
        var tiles = worldBlocks;
        if (tiles == null) {
            return;
        }

        ScavPlayer plr;
        if (!ScavPlayer.TryGetPlayerFromClientId(clientId, out plr)) {
            return;
        }
        if (server_chunksync_queue.Count > 200) {
            return; // Queue too long
        }



        reader.Get(out byte cornersyncchunk_x);
        reader.Get(out byte cornersyncchunk_y);

        var cornerPos = new Vector2Int(
                    cornersyncchunk_x,
                    cornersyncchunk_y
        );
        cornerPos.Clamp(
            new Vector2Int(0, 0),
            new Vector2Int(SYNCCHUNK_DIMENSION - SYNCREGION_SIZE, SYNCCHUNK_DIMENSION - SYNCREGION_SIZE)
        );
        if (cornerPos.x != cornersyncchunk_x || cornerPos.y != cornersyncchunk_y) {
            Plugin.log.LogWarning($"SUS: ChunksHash: {plr} -> Invalid chunk coordinate!");
            return;
        }


        var plrpos_blockspace = WorldGeneration.world.WorldToBlockPos(plr.pos);

        for (int y = 0; y < SYNCREGION_SIZE; y++) {
            for (int x = 0; x < SYNCREGION_SIZE; x++) {

                reader.Get(out uint hash);

                var chunkposX = cornerPos.x + x;
                var chunkposY = cornerPos.y + y;

                var blockposX = chunkposX * SYNCCHUNK_TILESIZE;
                var blockposY = chunkposY * SYNCCHUNK_TILESIZE;

                var my_hash = Server_ChunkHashes[chunkposX, chunkposY];
                if (my_hash == 0) {
                    Server_ChunkHashes[chunkposX, chunkposY] = my_hash = HashRegion(worldBlocks,
                        blockposX,
                        blockposX + SYNCCHUNK_TILESIZE,
                        blockposY,
                        blockposY + SYNCCHUNK_TILESIZE
                    );
                }


                if (hash != my_hash) {
                    // ok,  now the client says that the chunk is a little bit different than what we see on server
                    // queue this shit then

                    var blockposvec2 = new Vector2(blockposX, blockposY);
                    var dist = KM.DistanceSqr(blockposvec2, plrpos_blockspace);
                    var chunkPos = new Vector2UInt8(cornerPos.x + x, cornerPos.y + y);

                    Server_QueueChunkToSync(plr.playerId, dist, chunkPos);

                    if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(WorldGeneration.world.BlockToWorldPos(new Vector2Int(blockposX, blockposY)), $"S: {plr} doesn't know about this chunk  {cornersyncchunk_x},{cornersyncchunk_y}");

                }
            }
        }

    }
    static void Server_QueueChunkToSync(knetid plrId, float dist, Vector2UInt8 chunkPos) {

        var tupl = (
            plrId,
            dist,
            chunkPos
        );

        if (!server_chunksync_queue.Any(x => (x.Item1 == plrId && x.Item3 == chunkPos))) { // Not already queued
            int i;
            for (i = 0; i < server_chunksync_queue.Count; i++) {
                var check_against = server_chunksync_queue[i];
                if (check_against.Item2 < dist) {
                    continue;
                } else {
                    server_chunksync_queue.Insert(i, tupl);
                    return;
                }
            }
            if (i < 100) { // Limit the queue to 100
                server_chunksync_queue.Add(tupl);
            }
        }
    }












}
