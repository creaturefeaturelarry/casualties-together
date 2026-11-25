using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Multiupdater
{
    internal class Constants
    {
        //public static readonly string PluginsDirectory = String.Join(Path.DirectorySeparatorChar.ToString(), Environment.CurrentDirectory, "BepInEx", "plugins");
        //public static readonly string MultiDirectory = String.Join(Path.DirectorySeparatorChar.ToString(), Environment.CurrentDirectory, "BepInEx", "plugins", "KrokMP");
        //public static readonly string MultiPluginDLL = String.Join(Path.DirectorySeparatorChar.ToString(), Environment.CurrentDirectory, "BepInEx", "plugins", "KrokMP", "KrokoshaCasualtiesMP.dll");
        public static readonly string TempDirectory = String.Join(Path.DirectorySeparatorChar.ToString(), Path.GetTempPath(), "CTAutoUpdater");
        public const string StageDirectoryName = "MULTIPLAYER_STAGED";
        public const string MultiReleaseURL = @"https://api.github.com/repos/creaturefeaturelarry/casualties-together/releases/latest";
        // full game zips are MPMod_5.5.5_fullgame_whatever and demo zips are DEMO_5.4.2_whatever
        // both go up in the same release
#if FULLGAME
        public const string MultiArchiveNameContract = "MPMod_";
#else
        public const string MultiArchiveNameContract = "DEMO_";
#endif
        //public static readonly HashSet<string> VersionBlacklist = new HashSet<string>
        //{
        //    "v5.4.1.9",
        //    "v5.4.1.10",
        //    "v5.4.1.11",
        //    "v5.4.1.12"
        //};
    }
}
