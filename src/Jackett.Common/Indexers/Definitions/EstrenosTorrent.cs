using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AngleSharp.Html.Parser;
using Jackett.Common.Extensions;
using Jackett.Common.Helpers;
using Jackett.Common.Models;
using Jackett.Common.Models.IndexerConfig;
using Jackett.Common.Services.Interfaces;
using Jackett.Common.Utils.Clients;
using Newtonsoft.Json.Linq;
using NLog;
using static Jackett.Common.Models.IndexerConfig.ConfigurationData;
using WebClient = Jackett.Common.Utils.Clients.WebClient;

namespace Jackett.Common.Indexers.Definitions
{
    [ExcludeFromCodeCoverage]
    public class EstrenosTorrent : IndexerBase
    {
        public override string Id => "estrenostorrent";
        public override string Name => "EstrenosTorrent";
        public override string Description => "EstrenosTorrent is a SPANISH Public site for MOVIES / TV";

        private string _siteLink = "https://estrenostorrent.org/";
        private string SiteLinkSearch = "https://estrenostorrent.org/mvc/controllers/data.find.php";

        public override string SiteLink
        {
            get => _siteLink;
            protected set
            {
                _siteLink = value;
                SiteLinkSearch = value.TrimEnd('/') + "/mvc/controllers/data.find.php";
            }
        }

        public override string Language => "es-ES";
        public override string Type => "public";

        public override TorznabCapabilities TorznabCaps => SetCapabilities();

        private static Dictionary<string, long> EstimatedSizeByCategory => new Dictionary<string, long>
        {
            { EstrenosTorrentCatType.Pelicula, 2.Gigabytes() },
            { EstrenosTorrentCatType.Pelicula720, 5.Gigabytes() },
            { EstrenosTorrentCatType.Pelicula1080, 15.Gigabytes() },
            { EstrenosTorrentCatType.Pelicula4K, 30.Gigabytes() },
            { EstrenosTorrentCatType.Serie, 512.Megabytes() },
            { EstrenosTorrentCatType.Serie720, 1.Gigabytes() },
            { EstrenosTorrentCatType.Serie1080, 3.Gigabytes() },
            { EstrenosTorrentCatType.Serie4K, 8.Gigabytes() }
        };

        public EstrenosTorrent(IIndexerConfigurationService configService, WebClient w, Logger l,
                               IProtectionService ps, ICacheService cs)
            : base(configService: configService,
                   client: w,
                   logger: l,
                   p: ps,
                   cacheService: cs,
                   configData: new ConfigurationData())
        {
            configData.AddDynamic("flaresolverr", new DisplayInfoConfigurationItem("FlareSolverr", "This site may use Cloudflare DDoS Protection, therefore Jackett requires <a href=\"https://github.com/Jackett/Jackett#configuring-flaresolverr\" target=\"_blank\">FlareSolverr</a> to access it."));
            // Avoid Cloudflare too many requests limiter
            webclient.requestDelay = 2.1;
            webclient.EmulateBrowser = false;
        }

        private static TorznabCapabilities SetCapabilities()
        {
            var caps = new TorznabCapabilities
            {
                TvSearchParams = new List<TvSearchParam>
                {
                    TvSearchParam.Q, TvSearchParam.Season, TvSearchParam.Ep
                },
                MovieSearchParams = new List<MovieSearchParam>
                {
                    MovieSearchParam.Q
                }
            };
            caps.Categories.AddCategoryMapping(EstrenosTorrentCatType.Pelicula, TorznabCatType.MoviesSD, "Peliculas");
            caps.Categories.AddCategoryMapping(EstrenosTorrentCatType.Pelicula720, TorznabCatType.Movies, "Peliculas 720p");
            caps.Categories.AddCategoryMapping(EstrenosTorrentCatType.Pelicula1080, TorznabCatType.MoviesHD, "Peliculas 1080p");
            caps.Categories.AddCategoryMapping(EstrenosTorrentCatType.Pelicula4K, TorznabCatType.MoviesUHD, "Peliculas 4k");

            caps.Categories.AddCategoryMapping(EstrenosTorrentCatType.Serie, TorznabCatType.TVSD, "Series");
            caps.Categories.AddCategoryMapping(EstrenosTorrentCatType.Serie720, TorznabCatType.TV, "Series 720p");
            caps.Categories.AddCategoryMapping(EstrenosTorrentCatType.Serie1080, TorznabCatType.TVHD, "Series 1080p");
            caps.Categories.AddCategoryMapping(EstrenosTorrentCatType.Serie4K, TorznabCatType.TVUHD, "Series 4k");

            return caps;
        }

        public override async Task<IndexerConfigurationStatus> ApplyConfiguration(JToken configJson)
        {
            LoadValuesFromJson(configJson);

            var releases = await PerformQuery(new TorznabQuery());

            await ConfigureIfOK(string.Empty, releases.Any(), () =>
                                    throw new Exception("Could not find releases from this URL"));

            return IndexerConfigurationStatus.Completed;
        }

        protected override async Task<IEnumerable<ReleaseInfo>> PerformQuery(TorznabQuery query)
        {
            var searchToken = await GetSearchTokenAsync();

            query = SanitizeTorznabQuery(query);

            var body = new Dictionary<string, string>
            {
                { "pg", "" },
                { "token", searchToken },
                { "cidr", "" },
                { "c", "0" },
                { "q", query.SearchTerm },
                { "l", query.SearchTerm.IsNullOrWhiteSpace() ? "100" : "1000" },
            };

            var result = await RequestWithCookiesAndRetryAsync(url: SiteLinkSearch, data: body, method: RequestType.POST, referer: SiteLink);
            if (result.Status != HttpStatusCode.OK)
                throw new ExceptionWithConfigData(result.ContentString, configData);

            try
            {
                JObject jsonResponse = JObject.Parse(result.ContentString);
                var data = jsonResponse.SelectToken("data.datafinds.0");
                if (data == null)
                    return new List<ReleaseInfo>();

                return data.Values().Select(item => ExtractReleaseInfo(item as JObject, query)).ToList()
                           .Where(x => x != null);
            }
            catch (Exception ex)
            {
                OnParseError(result.ContentString, ex);
            }

            return new List<ReleaseInfo>();
        }

        public override async Task<byte[]> Download(Uri link)
        {
            var page = await RequestWithCookiesAndRetryAsync(link.ToString());
            if (page.Status != HttpStatusCode.OK)
            {
                throw new Exception($"Error fetching download page: HTTP {page.Status}");
            }

            var htmlParser = new HtmlParser();
            var doc = await htmlParser.ParseDocumentAsync(page.ContentString);

            // EstrenosTorrent provides a direct .torrent file link on the page
            var torrentLink = doc.QuerySelector("a[href*='.torrent']")?.GetAttribute("href");

            if (torrentLink.IsNullOrWhiteSpace())
            {
                throw new Exception("Could not find torrent download link on page.");
            }

            if (!torrentLink.StartsWith("http"))
            {
                torrentLink = new Uri(new Uri(SiteLink), torrentLink).ToString();
            }

            var torrentResult = await RequestWithCookiesAndRetryAsync(torrentLink);
            return torrentResult.ContentBytes;
        }

        private async Task<string> GetSearchTokenAsync()
        {
            var resultIdx = await RequestWithCookiesAndRetryAsync(SiteLink);
            var htmlParser = new HtmlParser();
            using var myDoc = htmlParser.ParseDocument(resultIdx.ContentString);
            return myDoc.QuerySelector("input[name='token']")?.GetAttribute("value");
        }

        private static TorznabQuery SanitizeTorznabQuery(TorznabQuery query)
        {
            // Eg. Marco.Polo.2014.S02E08
            // the season/episode part is already parsed by Jackett
            // query.SanitizedSearchTerm = Marco.Polo.2014.
            // query.Season = 2
            // query.Episode = 8
            var searchTerm = query.SanitizedSearchTerm;

            // Replace punctuation symbols with spaces
            // searchTerm = Marco Polo 2014
            searchTerm = Regex.Replace(searchTerm, @"[-._\(\)@/\\\[\]\+\%]", " ");
            searchTerm = searchTerm.Trim();

            // Parse the year and remove it from search
            // searchTerm = Marco Polo
            // query.Year = 2014
            var r = new Regex("([ ]+([0-9]{4}))$", RegexOptions.IgnoreCase);
            var m = r.Match(searchTerm);
            if (m.Success)
            {
                query.Year = int.Parse(m.Groups[2].Value);
                searchTerm = searchTerm.Replace(m.Groups[1].Value, "");
            }

            // Remove some words
            searchTerm = Regex.Replace(searchTerm, @"\b(espa[ñn]ol|spanish|castellano|spa)\b", "", RegexOptions.IgnoreCase);

            query.SearchTerm = searchTerm;
            return query;
        }

        private ReleaseInfo ExtractReleaseInfo(JObject item, TorznabQuery query)
        {
            var torrentName = item.SelectToken("torrentName")?.ToString();
            var guid = item.SelectToken("guid")?.ToString();
            var quality = item.SelectToken("calidad")?.ToString();
            var image = item.SelectToken("image")?.ToString();

            if (torrentName.IsNullOrWhiteSpace() || guid.IsNullOrWhiteSpace() || quality.IsNullOrWhiteSpace())
            {
                return null;
            }

            quality = ParseQuality(quality);
            var link = new Uri(new Uri(SiteLink), guid);
            var title = ParseTitle(torrentName, guid, quality);
            var episodes = GetEpisodesFromTitle(title);
            var category = ParseCategory(torrentName, guid, quality);

            var releaseInfo = new ReleaseInfo
            {
                Title = title,
                Link = link,
                Details = link,
                Guid = link,
                Category = MapTrackerCatToNewznab(category),
                PublishDate = DateTime.Now,
                Size = EstimatedSizeByCategory[category] * Math.Max(episodes.Count, 1),
                Seeders = 1,
                Peers = 2,
                DownloadVolumeFactor = 0,
                UploadVolumeFactor = 1
            };

            if (image.IsNotNullOrWhiteSpace() && !image.Contains("/no-imagen.jpg"))
            {
                var imageUrl = image.StartsWith("//") ? "https:" + image : image;
                releaseInfo.Poster = new Uri(imageUrl);
            }

            // Filter by category
            if (query.Categories.Any() && !query.Categories.Intersect(releaseInfo.Category).Any())
            {
                return null;
            }

            // Filter by Season
            if (query.Season.HasValue && !releaseInfo.Title.Contains("S" + query.Season.Value.ToString("D2")))
            {
                return null;
            }

            // Filter by Episode
            if (int.TryParse(query.Episode, out var episode) && episodes.Any() && !episodes.Contains(episode))
            {
                return null;
            }

            return releaseInfo;
        }

        private string ParseTitle(string torrentName, string guid, string quality)
        {
            var title = Regex.Replace(torrentName, @"(\- )?(Tem.|Temp.|Temporada)\s+?\d+?", "");
            title = Regex.Replace(title, @"\[(Esp|Spanish)\]", "", RegexOptions.IgnoreCase);
            title = Regex.Replace(title, @"\(?estrenostorrent\.org\)?", "", RegexOptions.IgnoreCase);
            title = Regex.Replace(title, @"\(?wolfmax4k\.com\)?", "", RegexOptions.IgnoreCase);
            title = Regex.Replace(title, @"\[?www\.[^\]]*\]?", "", RegexOptions.IgnoreCase);

            var seasonEpisode = ParseSeasonAndEpisode(torrentName, guid);
            if (seasonEpisode.IsNotNullOrWhiteSpace())
            {
                // Only replace Cap. if it could be parsed
                title = Regex.Replace(title, @"\[Cap\.(\s+)?(\d+)\]", "").Trim();
                title += " " + seasonEpisode;
            }

            // Remove the "quality" from the torrentName and
            // add it from the "quality" field of the api
            title = Regex.Replace(title, @"\[(.*)(HDTV|Bluray|4k|DVDRIP|BR-SCREENER|480p|1080p|720p)(.*)\]", "",
                                  RegexOptions.IgnoreCase);

            title = title + " [" + quality + "] SPANISH";

            return title.Trim();
        }

        private string ParseCategory(string torrentName, string guid, string quality)
        {
            // If the guid contains "serie" or the torrentName contains "Cap." it is a TV show
            var isTvShow = guid.Contains("serie") || (guid.Contains("/temporada-") && guid.Contains("/capitulo-")) ||
                           Regex.IsMatch(torrentName, @"Cap\.(\s+)?(\d+)", RegexOptions.IgnoreCase);

            string cat;
            if (isTvShow)
            {
                if (quality.Contains("720"))
                {
                    cat = EstrenosTorrentCatType.Serie720;
                }
                else if (quality.Contains("1080"))
                {
                    cat = EstrenosTorrentCatType.Serie1080;
                }
                else if (quality.ToLower().Contains("4k") || quality.ToLower().Contains("2160p"))
                {
                    cat = EstrenosTorrentCatType.Serie4K;
                }
                else
                {
                    cat = EstrenosTorrentCatType.Serie;
                }
            }
            else
            {
                if (quality.Contains("720"))
                {
                    cat = EstrenosTorrentCatType.Pelicula720;
                }
                else if (quality.Contains("1080"))
                {
                    cat = EstrenosTorrentCatType.Pelicula1080;
                }
                else if (quality.ToLower().Contains("4k") || quality.ToLower().Contains("2160p"))
                {
                    cat = EstrenosTorrentCatType.Pelicula4K;
                }
                else
                {
                    cat = EstrenosTorrentCatType.Pelicula;
                }
            }

            return cat;
        }

        private string ParseQuality(string quality)
        {
            return quality switch
            {
                "4KWebrip" => "WEBRip-2160p",
                _ => quality
            };
        }

        private string ParseSeasonAndEpisode(string torrentName, string guid)
        {
            var result = "";

            var matchSeason = new Regex(@"/temporada-(\d+)").Match(guid);
            if (matchSeason.Success)
            {
                result += "S" + matchSeason.Groups[1].Value.PadLeft(2, '0');
            }

            var matchEpisode = new Regex(@"/capitulo-(\d+)(-al-(\d+))?").Match(guid);
            if (matchSeason.Success && matchEpisode.Success)
            {
                result += "E" + matchEpisode.Groups[1].Value.PadLeft(2, '0');
                if (matchEpisode.Groups[3].Value.IsNotNullOrWhiteSpace())
                {
                    result += "-E" + matchEpisode.Groups[3].Value.PadLeft(2, '0');
                }
            }

            if (result.IsNotNullOrWhiteSpace())
            {
                return result;
            }

            // Fallback to torrentName "Cap." info
            // Cap. digits encode season+episode (e.g., Cap.204 = S02E04)
            var matchCaps = new Regex(@"Cap\.\s*([\d_]+)", RegexOptions.IgnoreCase).Match(torrentName);
            if (!matchCaps.Success)
            {
                return result;
            }

            var caps = matchCaps.Groups[1].Value.Trim().Split('_')
                                .Select(cap => cap.PadLeft(4, '0'))
                                .Where(cap => cap.Length == 4).ToList();

            if (!caps.Any())
            {
                return result;
            }

            var season = caps.First().Substring(0, 2);
            var episodes = caps.Select(cap => cap.Substring(2)).ToList();

            result = "S" + season + "E" + episodes.First();

            if (episodes.Count > 1)
            {
                result += "-E" + episodes.Last();
            }

            return result;
        }

        private List<int> GetEpisodesFromTitle(string title)
        {
            var vals = new Regex(@"E(\d+)").Matches(title).Cast<Match>().Select(m => int.Parse(m.Groups[1].Value)).ToList();

            if (vals.Count == 1)
            {
                return new List<int> { vals[0] };
            }

            if (vals.Count == 2 && vals[1] > vals[0])
            {
                return Enumerable.Range(vals[0], vals[1] - vals[0] + 1).ToList();
            }

            return new List<int>();
        }
    }

    internal static class EstrenosTorrentCatType
    {
        public static string Pelicula => "pelicula";
        public static string Pelicula720 => "pelicula720";
        public static string Pelicula1080 => "pelicula1080";
        public static string Pelicula4K => "pelicula4k";
        public static string Serie => "serie";
        public static string Serie720 => "serie720";
        public static string Serie1080 => "serie1080";
        public static string Serie4K => "serie4k";
    }
}
