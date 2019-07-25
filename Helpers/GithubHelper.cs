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
        private static readonly Regex PrUrlRegex = new Regex(@"^https://github\.com/([-A-Za-z0-9_.]+)/([-A-Za-z0-9_.]+)/pull/(\d+)(/files)?$");

        public static bool ParsePrUrl(string url, out PrInfo prInfo)
        {
            var match = PrUrlRegex.Match(url);
            if (!match.Success)
            {
                prInfo = null;
                return false;
            }

            prInfo = new PrInfo(
                match.Groups[1].Value,
                match.Groups[2].Value,
                match.Groups[3].Value);
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

    public class PrInfo
    {
        public string OwnerName { get; }
        public string RepoName { get; }
        public string PrNumber { get; }

        public PrInfo(string ownerName, string repoName, string prNumber)
        {
            OwnerName = ownerName;
            RepoName = repoName;
            PrNumber = prNumber;
        }
    }
}