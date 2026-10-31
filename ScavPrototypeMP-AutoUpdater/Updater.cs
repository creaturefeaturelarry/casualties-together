using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace Multiupdater
{
    internal static class Updater
    {
        public static async Task<bool> InitiateUpdate(string ArchiveURL)
        {
            AutoupdaterGUI.ShowUpdateButton = false;
            FileOperations.InitTempDirectory();
            Multiupdater.AutoUpdater.LogMessage("Downloading");
            string zipFile;
            try
            {
                zipFile = await FileOperations.DownloadArchive(ArchiveURL);
            }
            catch
            {
                Multiupdater.AutoUpdater.LogError("Github seems to be unreachable. Check your internet connection and github status.");
                goto CleanUp;
            }
            Multiupdater.AutoUpdater.LogMessage("Unpacking");
            try
            {
                FileOperations.UnzipFiles(zipFile);
            }
            catch
            {
                Multiupdater.AutoUpdater.LogError("Failed to unzip the update. Download might be corrupted. Retry.");
                goto CleanUp;
            }
            Multiupdater.AutoUpdater.LogMessage("Update staged! Restart the game to apply.");
            return true;
        CleanUp:
            FileOperations.ClearTempDirectory();
            AutoupdaterGUI.ShowUpdateButton = true;
            return false;
        }
    }
}
