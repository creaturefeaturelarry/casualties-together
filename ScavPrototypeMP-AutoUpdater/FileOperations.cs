using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace Multiupdater
{
    internal class FileOperations
    {
        private static string GetZipFilename(string url)
        {
            return url.Substring(url.LastIndexOf('/') + 1, url.LastIndexOf(".zip") + 3 - url.LastIndexOf('/'));
        }
        public static void InitTempDirectory()
        {
            if(Directory.Exists(Constants.TempDirectory))
            {
                Directory.Delete(Constants.TempDirectory, true);
            }
            Directory.CreateDirectory(Constants.TempDirectory);
        }
        public static void ClearTempDirectory()
        {
            Directory.Delete(Constants.TempDirectory, true);
        }
        public async static Task<string> DownloadArchive(string url)
        {
            using HttpClient client = new();
            Uri uri = new(url);
            string filename = FileOperations.GetZipFilename(url);
            string tempFolderFilePath = String.Join(Path.DirectorySeparatorChar.ToString(), Constants.TempDirectory, filename);
            client.Timeout = new TimeSpan(0, 0, 30);
            try
            {
                HttpResponseMessage response = await client.GetAsync(uri);
                using(FileStream fs = new FileStream(tempFolderFilePath, FileMode.Create))
                {
                    await response.Content.CopyToAsync(fs);
                }
                return tempFolderFilePath;
            }
            catch(TaskCanceledException ex) when(ex.InnerException is TimeoutException)
            {
                throw new TimeoutException();
            }
            catch(HttpRequestException)
            {
                throw new TimeoutException();
            }
        }
        public static void UnzipFiles(string zippedPath)
        {
            string directoryPath = String.Join(Path.DirectorySeparatorChar.ToString(), Constants.TempDirectory, Constants.StageDirectoryName);
            ZipFile.ExtractToDirectory(zippedPath, directoryPath);
        }
    }
}