using BepInEx.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using static Mono.Security.X509.X520;

namespace Multiupdater
{
    internal class HTTPHandler
    {
        public static async Task<(Version, string)> GetLatestRelease()
        {
            using(HttpClient client = new())
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd("MyGitHubClient/1.0");
                client.Timeout = new TimeSpan(0, 0, 10);
                HttpResponseMessage result;
                try
                {
                    result = await client.GetAsync(Constants.MultiReleaseURL);
                }
                catch(TaskCanceledException ex) when(ex.InnerException is TimeoutException)
                {
                    throw new TimeoutException();
                }
                catch(HttpRequestException)
                {
                    throw new TimeoutException();
                }
                if (!result.IsSuccessStatusCode)
                {
                    // rate limited or github is down. either way theres no update to get
                    throw new TimeoutException();
                }
                var str = await result.Content.ReadAsStringAsync();
                var json = JObject.Parse(str);
                var assets = json["assets"] as JArray;
                foreach (var value in assets)
                {
                    var name = value["name"].ToString();
                    if (!name.StartsWith(Constants.MultiArchiveNameContract) || !name.EndsWith(".zip"))
                    {
                        continue;
                    }
#if FULLGAME
                    if (!name.Contains("_fullgame"))
                    {
                        continue;
                    }
#endif
                    // the tag is the full game version and v5.5.5 has a 5.4.1 demo zip
                    // so read it off the zip name or the demo always looks outdated
                    var parts = name.Split('_');
                    if (parts.Length < 2 || !Version.TryParse(parts[1], out var version))
                    {
                        continue;
                    }
                    return (version, value["browser_download_url"].ToString());
                }
                throw new InvalidDataException("No suitable tag version retrieved!");
            }
        }
    }
}
