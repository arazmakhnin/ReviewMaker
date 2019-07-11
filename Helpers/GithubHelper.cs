using System;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ReviewMaker.Helpers
{
    public static class GithubHelper
    {
        private static readonly Regex PrUrlRegex = new Regex(@"^https://github\.com/trilogy-group/([-A-Za-z0-9.]+)/pull/(\d+)(/files)?$");

        public static bool ParsePrUrl(string url, out string repoName, out string prNumber)
        {
            var match = PrUrlRegex.Match(url);
            if (!match.Success)
            {
                repoName = null;
                prNumber = null;
                return false;
            }

            repoName = match.Groups[1].Value;
            prNumber = match.Groups[2].Value;
            return true;
        }

        public static async Task<JToken> Query(string query, string githubToken)
        {
            var obj = JsonConvert.SerializeObject(new
            {
                query = query
                    //.Replace("\r", string.Empty)
                    //.Replace("\n", string.Empty)
            });

            using (var client = new WebClient())
            {
                client.Headers["User-Agent"] = "ReviewMaker";
                client.Headers["Accept"] = "application/vnd.github.antiope-preview+json";
                client.Headers["Authorization"] = $"bearer {githubToken}";

                var result = await client.UploadStringTaskAsync("https://api.github.com/graphql", obj);

                return ((JToken)JsonConvert.DeserializeObject(result))["data"];
            }
        }
    }
}