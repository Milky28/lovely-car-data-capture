using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using LovelyCarDataCapture.Util;
using Newtonsoft.Json.Linq;

namespace LovelyCarDataCapture.Repo
{
    internal enum RepoLookupStatus { Disabled, Found, NotInRepo, Failed }

    internal sealed class RepoLookup
    {
        public RepoLookupStatus Status;
        /// <summary>Path relative to the repo's data/ folder, e.g. "iracing/acuransxevo22gt3.json".</summary>
        public string RelativePath;
        public string Text;
        public string Error;
        /// <summary>Other files in the same sim folder with the same carId.</summary>
        public List<string> SameCarId = new List<string>();

        public static RepoLookup Disabled() => new RepoLookup { Status = RepoLookupStatus.Disabled };
    }

    /// <summary>Reads public files from the Lovely Car Data repo on GitHub. Never writes anything.</summary>
    internal sealed class RepoClient
    {
        public const string Repo = "Lovely-Sim-Racing/lovely-car-data";
        private static readonly TimeSpan ManifestMaxAge = TimeSpan.FromMinutes(30);
        private static readonly HttpClient Http;

        private readonly object _lock = new object();
        private Task<JObject> _manifest;
        private string _manifestBranch;
        private DateTime _manifestFetchedUtc;

        static RepoClient()
        {
            // .NET Framework 4.8 inside SimHub may not offer TLS 1.2 by default; GitHub requires it.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            Http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("LovelyCarDataCapture-SimHubPlugin");
        }

        private static string RawUrl(string branch, string path) =>
            "https://raw.githubusercontent.com/" + Repo + "/" + Uri.EscapeDataString(branch) + "/" + path;

        public async Task<RepoLookup> FindAsync(string gameName, string carId, string branch)
        {
            var sim = Slug.Make(gameName);
            try
            {
                var manifest = await GetManifest(branch).ConfigureAwait(false);
                var entries = (manifest["cars"]?[sim] as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
                var slugPath = sim + "/" + Slug.Make(carId) + ".json";

                // Files aren't always named after their carId, so match on carId first.
                var byId = entries.Where(e => (string)e["carId"] == carId).ToList();
                var match = byId.FirstOrDefault(e => (string)e["path"] == slugPath)
                    ?? byId.FirstOrDefault()
                    ?? entries.FirstOrDefault(e => (string)e["path"] == slugPath);

                if (match == null) return new RepoLookup { Status = RepoLookupStatus.NotInRepo, RelativePath = slugPath };

                var path = (string)match["path"];
                var text = await Http.GetStringAsync(RawUrl(branch, "data/" + path)).ConfigureAwait(false);
                return new RepoLookup
                {
                    Status = RepoLookupStatus.Found,
                    RelativePath = path,
                    Text = text,
                    SameCarId = byId.Select(e => (string)e["path"]).Where(p => p != path).ToList(),
                };
            }
            catch (Exception ex)
            {
                if (ex is AggregateException agg) ex = agg.Flatten().InnerException ?? ex;
                return new RepoLookup { Status = RepoLookupStatus.Failed, Error = ex.Message };
            }
        }

        private Task<JObject> GetManifest(string branch)
        {
            lock (_lock)
            {
                bool fresh = _manifest != null && _manifestBranch == branch && DateTime.UtcNow - _manifestFetchedUtc < ManifestMaxAge
                    && !_manifest.IsFaulted && !_manifest.IsCanceled;
                if (!fresh)
                {
                    _manifestBranch = branch;
                    _manifestFetchedUtc = DateTime.UtcNow;
                    _manifest = FetchManifest(branch);
                }
                return _manifest;
            }
        }

        private static async Task<JObject> FetchManifest(string branch)
        {
            var text = await Http.GetStringAsync(RawUrl(branch, "data/manifest.json")).ConfigureAwait(false);
            return JObject.Parse(text);
        }
    }
}
