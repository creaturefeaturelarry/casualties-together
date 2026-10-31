using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

// turns out this is obsolete when i jsut hide the lore dump ui element

namespace Together
{
    // skip intro lore dump
    [HarmonyPatch(typeof(PreRunScript), nameof(PreRunScript.TryLore))]
    public static class SkipIntroLoreDumpPatch
    {
        static void Prefix()
        {
#if !DEBUG
            if (!(Plugin.юзер_прошаренный)) { 
                return;
            }
#endif

            PreRunScript.didIntro = true; 

        }
    }
}
