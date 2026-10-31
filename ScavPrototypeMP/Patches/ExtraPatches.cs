using HarmonyLib;
using CasualtiesTogetherUtils;
using LiteNetLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Together;




internal static class ExtraPatches {

    public static void ApplyPatches(Harmony harm) {
#if FULLGAME

#endif
    }
    public static void UnpatchExtraPatches(Harmony harm) {
        try {
#if FULLGAME

#endif
        } catch (Exception e) {
            log.error(e);
        }
    }

}
