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
        public const string MultiArchiveNameContract = "CasualtiesTogether";
        //public static readonly HashSet<string> VersionBlacklist = new HashSet<string>
        //{
        //    "v5.4.1.9",
        //    "v5.4.1.10",
        //    "v5.4.1.11",
        //    "v5.4.1.12"
        //};
    }
}
