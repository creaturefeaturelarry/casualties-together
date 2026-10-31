// by dannad



using Mono.Cecil;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;

namespace autoupdater_patcher {

    public static class AutoUpdaterPatcher {


        public static IEnumerable<string> TargetDLLs { get; } = new[] { "Assembly-CSharp.dll" };
        public static readonly string OSTempTrimmed = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        public static readonly string TempDirectory = String.Join(Path.DirectorySeparatorChar.ToString(), OSTempTrimmed, "CTAutoUpdater");
        public static readonly string StagedDirectory = String.Join(Path.DirectorySeparatorChar.ToString(), OSTempTrimmed, "CTAutoUpdater", "MULTIPLAYER_STAGED");
        public static readonly string UnpackDirectory = String.Join(Path.DirectorySeparatorChar.ToString(), Environment.CurrentDirectory);
        public static readonly string PluginDirectory = String.Join(Path.DirectorySeparatorChar.ToString(), Environment.CurrentDirectory, "BepInEx", "plugins");
        public static readonly string PatcherDirectory = String.Join(Path.DirectorySeparatorChar.ToString(), Environment.CurrentDirectory, "BepInEx", "patchers");
        public static readonly string[] OldPluginDLLs = new string[]
        {
            //String.Join(Path.DirectorySeparatorChar.ToString(), Environment.CurrentDirectory, "BepInEx", "patchers", "KrokMP", "autoupdater_patcher.dll"),
            String.Join(Path.DirectorySeparatorChar.ToString(), Environment.CurrentDirectory, "BepInEx", "plugins", "KrokMP", "KrokoshaCasualtiesMP.dll"),
            String.Join(Path.DirectorySeparatorChar.ToString(), Environment.CurrentDirectory, "BepInEx", "plugins", "KrokMP", "Multiupdater.dll"),
            String.Join(Path.DirectorySeparatorChar.ToString(), Environment.CurrentDirectory, "BepInEx", "plugins", "KrokMP", "opus.dll"),
            String.Join(Path.DirectorySeparatorChar.ToString(), Environment.CurrentDirectory, "BepInEx", "plugins", "KrokMP", "OpusSharp.Core.dll"),
            String.Join(Path.DirectorySeparatorChar.ToString(), Environment.CurrentDirectory, "BepInEx", "plugins", "KrokMP", "steam_api64.dll"),
            String.Join(Path.DirectorySeparatorChar.ToString(), Environment.CurrentDirectory, "BepInEx", "plugins", "KrokMP", "Steamworks.NET.dll"),
            String.Join(Path.DirectorySeparatorChar.ToString(), Environment.CurrentDirectory, "BepInEx", "plugins", "KrokMP", "LiteNetLib.dll"),
            String.Join(Path.DirectorySeparatorChar.ToString(), Environment.CurrentDirectory, "BepInEx", "plugins", "KrokoshaCasualtiesMP.dll"),
            String.Join(Path.DirectorySeparatorChar.ToString(), Environment.CurrentDirectory, "BepInEx", "plugins", "opus.dll"),
            String.Join(Path.DirectorySeparatorChar.ToString(), Environment.CurrentDirectory, "BepInEx", "plugins", "OpusSharp.Core.dll"),
            String.Join(Path.DirectorySeparatorChar.ToString(), Environment.CurrentDirectory, "BepInEx", "plugins", "Unity.Netcode.Components.dll"),
            String.Join(Path.DirectorySeparatorChar.ToString(), Environment.CurrentDirectory, "BepInEx", "plugins", "Unity.Netcode.Runtime.dll"),
            String.Join(Path.DirectorySeparatorChar.ToString(), Environment.CurrentDirectory, "BepInEx", "plugins", "Unity.Networking.Transport.dll"),
        };
        public static readonly HashSet<string> FileExceptions = new HashSet<string>
        {
            typeof(AutoUpdaterPatcher).Assembly.Location,
        };
        public static void Patch(AssemblyDefinition assembly) {

            //autoupdater_patcher
            //CT_AutoUpdateApplier

            /* // BepInEx already sorts by versions
             try {
                var version = Assembly.GetExecutingAssembly().GetName().Version;



                IEnumerable<string> FindAssemblies(
                   string rootDirectory,
                   string targetNamespace,
                   Version minimumVersion
               ) {
                    foreach (var file in Directory.EnumerateFiles(rootDirectory, "*.dll", SearchOption.AllDirectories)) {
                        AssemblyName assemblyName;

                        // Skip native DLLs
                        try {
                            assemblyName = AssemblyName.GetAssemblyName(file);
                        } catch {
                            continue;
                        }

                        if (assemblyName.Version == null ||
                            assemblyName.Version <= minimumVersion) {
                            continue;
                        }

                        bool containsNamespace = false;
                        try {
                            var loadedassembly = Assembly.LoadFrom(file);

                            containsNamespace = loadedassembly
                                .GetTypes()
                                .Any(t => t.Namespace == targetNamespace);

                        } catch (ReflectionTypeLoadException ex) {
                            containsNamespace = containsNamespace || (ex.Types.Any(t => t?.Namespace == targetNamespace));
                        } catch {
                            // Ignore assemblies that cannot be loaded
                            containsNamespace = false;
                        }
                        if (containsNamespace)
                            yield return file;
                    }
                }

                var ass = FindAssemblies(PatcherDirectory, "autoupdater_patcher", version);

                if (ass.Count() > 0) { // There is an updater newer 
                    return;
                }

            } catch (Exception e) {
            }
             */




            string LookForTargetDirectory(string path) {
                foreach (var directory in Directory.GetDirectories(path)) {
                    if (directory.Contains("BepInEx")) {
                        return Path.GetDirectoryName(directory);
                    } else {
                        string result = LookForTargetDirectory(directory);
                        if (result != null) {
                            return result;
                        }
                    }
                }
                return null;
            }
            void CloneDirectory(string root, string dest) {
                foreach (var directory in Directory.GetDirectories(root)) {
                    try {
                        var newDirectory = Path.Combine(dest, Path.GetFileName(directory));
                        Directory.CreateDirectory(newDirectory);
                        CloneDirectory(directory, newDirectory);
                    } catch (Exception ex) { }
                }
                foreach (var rootPath in Directory.GetFiles(root)) {
                    try {
                        string destinationPath = Path.Combine(dest, Path.GetFileName(rootPath));
                        if (!FileExceptions.Contains(destinationPath)) {
                            File.Copy(rootPath, destinationPath, true);
                        }
                    } catch (Exception ex) { }
                }
            }
            try {
                try {
                    foreach (var path in new string[] {
                            //String.Join(Path.DirectorySeparatorChar.ToString(), Environment.CurrentDirectory, "BepInEx", "patchers", "autoupdater_patcher.dll"),
                            //String.Join(Path.DirectorySeparatorChar.ToString(), Environment.CurrentDirectory, "BepInEx", "patchers", "KrokMP", "autoupdater_patcher.dll"),
                            String.Join(Path.DirectorySeparatorChar.ToString(), Environment.CurrentDirectory, "BepInEx", "plugins", "KrokMP", "KrokoshaCasualtiesMP.dll"),
                            String.Join(Path.DirectorySeparatorChar.ToString(), Environment.CurrentDirectory, "BepInEx", "plugins", "KrokMP", "Multiupdater.dll"),
                            String.Join(Path.DirectorySeparatorChar.ToString(), Environment.CurrentDirectory, "BepInEx", "plugins", "KrokMP", "LiteNetLib.dll"),
                            String.Join(Path.DirectorySeparatorChar.ToString(), Environment.CurrentDirectory, "BepInEx", "plugins", "KrokMP", "OpusSharp.Core.dll"),
                            //String.Join(Path.DirectorySeparatorChar.ToString(), Environment.CurrentDirectory, "BepInEx", "plugins", "KrokoshaCasualtiesMP.dll"),
                        }
                    ) {
                        try {
                            if (File.Exists(path)) {
                                File.Delete(path);
                            }
                        } catch (Exception e) {
                            // It can't delete files that are in-use
                        }
                    }
                } catch (Exception e) { }

                if (!Directory.Exists(StagedDirectory)) {
                    return;
                }
                foreach (string path in OldPluginDLLs) {
                    if (File.Exists(path)) {
                        try {
                            File.Delete(path);
                        } catch (Exception e) { }
                    }
                }
                string CloneTargetDirectory = LookForTargetDirectory(StagedDirectory);
                if (CloneTargetDirectory == null) {
                    throw new Exception("Could not find the mod root directory!");
                }
                CloneDirectory(CloneTargetDirectory, UnpackDirectory);
                Directory.Delete(TempDirectory, true);
            } catch (Exception ex) {
                File.WriteAllText(Path.GetTempPath() + $"TOGETHER_AUTOUPDATER_LOG_{DateTime.Now.Ticks.ToString()}.txt", ex.ToString() + "\n\n" + LookForTargetDirectory(StagedDirectory).ToString());
            }
        }
    }
}





