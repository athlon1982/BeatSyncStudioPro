using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace BeatSyncStudioPro
{
    /// <summary>A station found in the directory/search, shown to the
    /// user by name and location: "Cat Country 105.3 — WJEN · Rutland".</summary>
    public sealed class DirectoryStation
    {
        public string Name { get; set; } = "";
        public string City { get; set; } = "";
        public string Format { get; set; } = "";
        public string Url { get; set; } = "";

        public string Display =>
            Name + (City.Length > 0 ? " · " + City : "") + (Format.Length > 0 ? " · " + Format : "");
    }

    /// <summary>
    /// Vermont station directory (v3.100) + online search. The bundled
    /// Vermont list is hand-verified: every stream URL was fetched and
    /// confirmed playing live audio on 2026-10-23. Online search uses
    /// the free Radio Browser directory (no account, no key) for any
    /// station name worldwide.
    /// </summary>
    public static class RadioDirectory
    {
        public static List<DirectoryStation> Vermont() => new List<DirectoryStation>
        {
            new DirectoryStation { Name = "Rock 94.5 — WDVT", City = "Rutland", Format = "Classic rock", Url = "https://ais-sa1.streamon.fm/7822_96k.aac" },
            new DirectoryStation { Name = "Z97.1 — WZRT", City = "Rutland", Format = "Top 40", Url = "https://ais-sa1.streamon.fm/7826_128k.aac" },
            new DirectoryStation { Name = "Cat Country 105.3 — WJEN", City = "Rutland", Format = "Country", Url = "https://ais-sa1.streamon.fm/7823_96k.aac" },
            new DirectoryStation { Name = "Rad-Dad Radio", City = "Wallingford", Format = "Your own station", Url = "http://66.220.235.250:8000/stream" },
            new DirectoryStation { Name = "Vermont Public — News", City = "Burlington · statewide", Format = "News / NPR", Url = "https://vpr.streamguys1.com/vpr64.aac" },
            new DirectoryStation { Name = "Vermont Public — Classical", City = "Burlington · statewide", Format = "Classical", Url = "https://vprclassical.streamguys1.com/vprclassical64.aac" },
            new DirectoryStation { Name = "Vermont Public — Jazz", City = "Statewide", Format = "Jazz", Url = "https://vpr.streamguys1.com/vprjazz64.aac" },
            new DirectoryStation { Name = "BBC World Service (via Vermont Public)", City = "Statewide", Format = "World news", Url = "https://vprbbc.streamguys1.com/vprbbc24.mp3" },
            new DirectoryStation { Name = "WDEV 96.1 / 550", City = "Waterbury", Format = "News / talk", Url = "https://playerservices.streamtheworld.com/api/livestream-redirect/WDEVFM.mp3" },
            new DirectoryStation { Name = "The Point 104.7 — WNCS", City = "Montpelier", Format = "Independent / AAA", Url = "https://nebcoradio.com:8443/WNCS" },
            new DirectoryStation { Name = "WRUV 90.1", City = "Burlington (UVM)", Format = "College / freeform", Url = "http://icecast.uvm.edu:8005/wruv_fm_128" },
            new DirectoryStation { Name = "WGDR 91.1", City = "Plainfield", Format = "Community radio", Url = "https://wgdr.broadcasttool.stream/wgdr_128" },
            new DirectoryStation { Name = "WMRW 94.5", City = "Warren", Format = "Community radio", Url = "http://69.54.28.12:8951/" },
            new DirectoryStation { Name = "Alt Vintage", City = "Vermont", Format = "Oldies", Url = "https://streaming.live365.com/a89825" }
        };

        /// <summary>Search the bundled Vermont list by name, city or format.</summary>
        public static List<DirectoryStation> SearchLocal(string query)
        {
            var all = Vermont();
            if (string.IsNullOrWhiteSpace(query)) return all;
            string q = query.Trim().ToLowerInvariant();
            return all.Where(s => (s.Name + " " + s.City + " " + s.Format).ToLowerInvariant().Contains(q)).ToList();
        }
    }

    /// <summary>Radio Browser (radio-browser.info) — the free community
    /// station directory. No account or key. Mirrors are tried in order.</summary>
    public static class RadioBrowser
    {
        private static readonly string[] Mirrors =
        {
            "https://de1.api.radio-browser.info",
            "https://de2.api.radio-browser.info"
        };

        /// <summary>Parse a Radio Browser JSON array (pure — tested).</summary>
        public static List<DirectoryStation> ParseStations(string json)
        {
            var results = new List<DirectoryStation>();
            var seen = new HashSet<string>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    string url = Get(el, "url_resolved");
                    if (url.Length == 0) url = Get(el, "url");
                    if (!RadioStore.IsValidStreamUrl(url) || !seen.Add(url)) continue;
                    string name = Get(el, "name").Trim();
                    if (name.Length == 0) continue;
                    string state = Get(el, "state").Trim();
                    string country = Get(el, "country").Trim();
                    string tags = Get(el, "tags").Trim();
                    string city = state.Length > 0 ? state : country;
                    string format = tags.Split(',')[0].Trim();
                    results.Add(new DirectoryStation { Name = name, City = city, Format = format, Url = url });
                }
            }
            catch { }
            return results;
        }

        private static string Get(JsonElement el, string prop) =>
            el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        private static async Task<string?> Fetch(string path)
        {
            foreach (var mirror in Mirrors)
            {
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("BeatSyncStudioPro/3.100");
                    var resp = await http.GetAsync(mirror + path);
                    if (resp.IsSuccessStatusCode) return await resp.Content.ReadAsStringAsync();
                }
                catch { }
            }
            return null;
        }

        /// <summary>All listed Vermont stations from the online directory.</summary>
        public static async Task<List<DirectoryStation>> VermontOnline()
        {
            string? json = await Fetch("/json/stations/search?state=Vermont&countrycode=US&hidebroken=true&limit=200");
            return json == null ? new List<DirectoryStation>() : ParseStations(json);
        }

        /// <summary>Search any station name, anywhere.</summary>
        public static async Task<List<DirectoryStation>> SearchByName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return new List<DirectoryStation>();
            string? json = await Fetch("/json/stations/search?name=" + Uri.EscapeDataString(name.Trim()) + "&hidebroken=true&limit=60");
            return json == null ? new List<DirectoryStation>() : ParseStations(json);
        }
    }
}
