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
            Multiupdater.AutoUpdater.LogMessage(UpdaterText.Get("au_downloading"));
            string zipFile;
            try
            {
                zipFile = await FileOperations.DownloadArchive(ArchiveURL);
            }
            catch
            {
                Multiupdater.AutoUpdater.LogError(UpdaterText.Get("au_github_unreachable_long"));
                goto CleanUp;
            }
            Multiupdater.AutoUpdater.LogMessage(UpdaterText.Get("au_unpacking"));
            try
            {
                FileOperations.UnzipFiles(zipFile);
            }
            catch
            {
                Multiupdater.AutoUpdater.LogError(UpdaterText.Get("au_unzip_failed"));
                goto CleanUp;
            }
            Multiupdater.AutoUpdater.LogMessage(UpdaterText.Get("au_staged"));
            return true;
        CleanUp:
            FileOperations.ClearTempDirectory();
            AutoupdaterGUI.ShowUpdateButton = true;
            return false;
        }
    }
}
