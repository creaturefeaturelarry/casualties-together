using System;

namespace Together;

// state kept per player that nothing else removes when the player leaves.
static class PlayerLeaveCleanup {
    [CTPostInit]
    static void Init() {
        ScavPlayer.OnPlayerLeft += Forget;
    }

    static void Forget(ScavPlayer leaving) {
        if (leaving == null) {
            return;
        }

        knetid player = leaving.playerId;
        CraftCommit.Forget(player);
        MoveCommit.Forget(player);
        FallCatch.Forget(player.id);
        Body_Attack_MultiplayerPatch.Forget(player);
        ServerMain.ForgetSpawnBusy(player);
    }
}
