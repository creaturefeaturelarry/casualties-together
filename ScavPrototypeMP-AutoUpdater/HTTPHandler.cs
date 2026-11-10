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
                var str = await result.Content.ReadAsStringAsync();
                var json = JObject.Parse(str);
                var version = Version.Parse(json["tag_name"].ToString().TrimStart('v'));
                var assets = json["assets"] as JArray;
                foreach (var value in assets)
                {
                    var name = value["name"].ToString();
                    if (name.StartsWith(Constants.MultiArchiveNameContract) &&
#if FULLGAME
                        name.Contains("_fullgame")
#else
                        name.Contains("_demo")
#endif
                        ) {
                        return (version, value["browser_download_url"].ToString());
                    }
                }
                throw new InvalidDataException("No suitable tag version retrieved!");
            }
        }
    }
}
