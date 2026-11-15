using System;

// no cucorelib settings tab. the bepinex cfg already has the slot and backup count.
internal static class CoreLibIntegration {
    internal static void TryRegister() {
        if (SpriteReplacerPlugin.Log != null) {
            SpriteReplacerPlugin.Log.LogInfo("Skin settings stay in the BepInEx config.");
        }
    }

    internal static void StoreActiveSlot(string slot) {
    }

    internal static void StoreBackupCount(int count) {
    }
}