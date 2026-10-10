using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
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
    public class GranTorrent : IndexerBase
    {
        public override string Id => "grantorrent";
        public override string Name => "GranTorrent";
        public override string Description => "GranTorrent is a SPANISH Public site for MOVIES / TV";

        private string _siteLink = "https://grantorrent.foo/";

        public override string SiteLink
        {
            get => _siteLink;
            protected set => _siteLink = value;
        }

        public override string[] AlternativeSiteLinks => new[]
        {
            "https://grantorrent.foo/",
            "https://pasateatorrent.org/",
            "https://www1.grantorrent.lol/",
            "https://www2.grantorrent.lol/",
            "https://www3.grantorrent.lol/"
        };

        public override string Language => "es-ES";
        public override string Type => "public";

        public override TorznabCapabilities TorznabCaps => SetCapabilities();

        private string SearchUrl => SiteLink.TrimEnd('/') + "/wp-admin/admin-ajax.php";

        private static Dictionary<string, long> EstimatedSizeByCategory => new Dictionary<string, long>
        {
            { GranTorrentCatType.Pelicula, 2.Gigabytes() },
            { GranTorrentCatType.Pelicula720, 5.Gigabytes() },
            { GranTorrentCatType.Pelicula1080, 15.Gigabytes() },
            { GranTorrentCatType.Pelicula4K, 30.Gigabytes() },
            { GranTorrentCatType.Serie, 512.Megabytes() },
            { GranTorrentCatType.Serie720, 1.Gigabytes() },
            { GranTorrentCatType.Serie1080, 3.Gigabytes() },
            { GranTorrentCatType.Serie4K, 8.Gigabytes() }
        };

        public GranTorrent(IIndexerConfigurationService configService, WebClient w, Logger l,
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
            caps.Categories.AddCategoryMapping(GranTorrentCatType.Pelicula, TorznabCatType.MoviesSD, "Peliculas");
            caps.Categories.AddCategoryMapping(GranTorrentCatType.Pelicula720, TorznabCatType.Movies, "Peliculas 720p");
            caps.Categories.AddCategoryMapping(GranTorrentCatType.Pelicula1080, TorznabCatType.MoviesHD, "Peliculas 1080p");
            caps.Categories.AddCategoryMapping(GranTorrentCatType.Pelicula4K, TorznabCatType.MoviesUHD, "Peliculas 4k");

            caps.Categories.AddCategoryMapping(GranTorrentCatType.Serie, TorznabCatType.TVSD, "Series");
            caps.Categories.AddCategoryMapping(GranTorrentCatType.Serie720, TorznabCatType.TV, "Series 720p");
            caps.Categories.AddCategoryMapping(GranTorrentCatType.Serie1080, TorznabCatType.TVHD, "Series 1080p");
            caps.Categories.AddCategoryMapping(GranTorrentCatType.Serie4K, TorznabCatType.TVUHD, "Series 4k");

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
            var releases = new List<ReleaseInfo>();

            query = SanitizeTorznabQuery(query);
            var searchTerm = query.SearchTerm;

            if (searchTerm.IsNullOrWhiteSpace())
            {
                // Browse mode: scrape the homepage for recent releases
                releases = await FetchHomepageReleasesAsync(query);
            }
            else
            {
                // Search mode: use the autocomplete API then fetch detail pages
                releases = await SearchViaAutocompleteAsync(searchTerm, query);
            }

            return releases;
        }

        private async Task<List<ReleaseInfo>> SearchViaAutocompleteAsync(string searchTerm, TorznabQuery query)
        {
            var releases = new List<ReleaseInfo>();

            var searchUrl = SearchUrl + "?action=search_autocomplete&q=" + WebUtility.UrlEncode(searchTerm);
            var result = await RequestWithCookiesAndRetryAsync(searchUrl, referer: SiteLink);

            if (result.Status != HttpStatusCode.OK)
                return releases;

            JArray searchResults;
            try
            {
                searchResults = JArray.Parse(result.ContentString);
            }
            catch
            {
                return releases;
            }

            if (searchResults == null || !searchResults.Any())
                return releases;

            foreach (var item in searchResults)
            {
                var title = item.Value<string>("title");
                var type = item.Value<string>("type");
                var url = item.Value<string>("url");
                var thumbnail = item.Value<string>("thumbnail");

                if (title.IsNullOrWhiteSpace() || url.IsNullOrWhiteSpace())
                    continue;

                var isSeries = type?.Equals("Serie", StringComparison.OrdinalIgnoreCase) == true;

                try
                {
                    var pageReleases = await FetchDetailPageReleasesAsync(url, title, isSeries, thumbnail, query);
                    releases.AddRange(pageReleases);
                }
                catch (Exception ex)
                {
                    logger.Warn($"GranTorrent: Error fetching detail page {url}: {ex.Message}");
                }
            }

            return releases;
        }

        private async Task<List<ReleaseInfo>> FetchDetailPageReleasesAsync(string pageUrl, string title,
            bool isSeries, string thumbnail, TorznabQuery query)
        {
            var releases = new List<ReleaseInfo>();

            var pageResult = await RequestWithCookiesAndRetryAsync(pageUrl, referer: SiteLink);
            if (pageResult.Status != HttpStatusCode.OK)
                return releases;

            var htmlParser = new HtmlParser();
            using var doc = htmlParser.ParseDocument(pageResult.ContentString);

            var rows = doc.QuerySelectorAll("tbody tr");
            if (rows == null || !rows.Any())
                return releases;

            foreach (var row in rows)
            {
                var cells = row.QuerySelectorAll("td");
                if (cells == null || cells.Length < 3)
                    continue;

                var downloadLink = row.QuerySelector("a.linktorrent");
                if (downloadLink == null)
                    continue;

                var dataSrc = downloadLink.GetAttribute("data-src");
                if (dataSrc.IsNullOrWhiteSpace())
                    continue;

                // Decode the base64 data-src to get the direct torrent URL
                string torrentUrl;
                try
                {
                    torrentUrl = Encoding.UTF8.GetString(Convert.FromBase64String(dataSrc));
                }
                catch
                {
                    // Fallback to super-enlace href if data-src decode fails
                    torrentUrl = downloadLink.GetAttribute("href");
                    if (torrentUrl.IsNullOrWhiteSpace())
                        continue;
                }

                string episodeInfo = null;
                string quality = null;
                string formato = null;
                long size = 0;

                if (isSeries)
                {
                    // Series table: Episodio | Peso | Clave | Download
                    if (cells.Length >= 3)
                    {
                        episodeInfo = cells[0].TextContent.Trim(); // e.g. "2x1", "1x01"
                        var sizeText = cells[1].TextContent.Trim(); // e.g. "0.33GB", "900 mb"
                        size = ParseSize(sizeText);
                    }
                }
                else
                {
                    // Movie table: Formato | Calidad | Peso | Download
                    if (cells.Length >= 3)
                    {
                        formato = cells[0].TextContent.Trim(); // e.g. "MP4"
                        quality = cells[1].TextContent.Trim(); // e.g. "720p"
                        var sizeText = cells[2].TextContent.Trim(); // e.g. "3.25GB."
                        size = ParseSize(sizeText);
                    }
                }

                // Build release title
                var releaseTitle = BuildReleaseTitle(title, isSeries, episodeInfo, quality, formato);

                // Determine category
                var category = DetermineCategory(isSeries, quality, episodeInfo, size);

                // Parse season and episode for filtering
                int? season = null;
                int? episode = null;
                if (isSeries && episodeInfo.IsNotNullOrWhiteSpace())
                {
                    ParseEpisodeInfo(episodeInfo, out season, out episode);
                }

                // Apply season/episode filters
                if (query.Season.HasValue && season.HasValue && season.Value != query.Season.Value)
                    continue;

                if (int.TryParse(query.Episode, out var queryEp) && episode.HasValue && episode.Value != queryEp)
                    continue;

                var detailsUri = new Uri(pageUrl);
                var downloadUri = new Uri(torrentUrl);

                var releaseInfo = new ReleaseInfo
                {
                    Title = releaseTitle,
                    Link = downloadUri,
                    Details = detailsUri,
                    Guid = downloadUri,
                    Category = MapTrackerCatToNewznab(category),
                    PublishDate = DateTime.Now,
                    Size = size > 0 ? size : EstimatedSizeByCategory[category],
                    Seeders = 1,
                    Peers = 2,
                    DownloadVolumeFactor = 0,
                    UploadVolumeFactor = 1
                };

                if (thumbnail.IsNotNullOrWhiteSpace())
                {
                    try
                    {
                        releaseInfo.Poster = new Uri(thumbnail);
                    }
                    catch
                    {
                        // Ignore invalid thumbnail URLs
                    }
                }

                // Filter by category
                if (query.Categories.Any() && !query.Categories.Intersect(releaseInfo.Category).Any())
                    continue;

                releases.Add(releaseInfo);
            }

            return releases;
        }

        private async Task<List<ReleaseInfo>> FetchHomepageReleasesAsync(TorznabQuery query)
        {
            var releases = new List<ReleaseInfo>();

            var result = await RequestWithCookiesAndRetryAsync(SiteLink);
            if (result.Status != HttpStatusCode.OK)
                return releases;

            var htmlParser = new HtmlParser();
            using var doc = htmlParser.ParseDocument(result.ContentString);

            // Homepage cards: each item is a link containing an img with class "rounded-xl"
            var images = doc.QuerySelectorAll("img.rounded-xl");
            var processedUrls = new HashSet<string>();

            foreach (var img in images)
            {
                var card = img.Closest("a");
                if (card == null)
                    continue;

                var href = card.GetAttribute("href");
                if (href.IsNullOrWhiteSpace())
                    continue;

                // Skip non-detail pages
                if (href.Contains("/categoria/") || href.Contains("/contacto/") ||
                    href.Contains("/page/") || href.Contains("/wp-content/") ||
                    href.Contains("/wp-json/") || href.Contains("/wp-admin/"))
                    continue;

                // Skip listing pages
                var trimmedHref = href.TrimEnd('/');
                if (trimmedHref.EndsWith("/peliculas") || trimmedHref.EndsWith("/series_p") ||
                    trimmedHref.EndsWith("/series"))
                    continue;

                if (processedUrls.Contains(href))
                    continue;

                processedUrls.Add(href);

                // Infer if it's a series by URL pattern
                var isSeries = href.Contains("/series/");
                var cardTitle = img.GetAttribute("alt");

                if (cardTitle.IsNullOrWhiteSpace())
                    continue;

                // Limit homepage items to avoid too many requests
                if (processedUrls.Count > 20)
                    break;

                try
                {
                    var cardThumbnail = img.GetAttribute("src");
                    var pageReleases = await FetchDetailPageReleasesAsync(href, cardTitle, isSeries, cardThumbnail, query);
                    releases.AddRange(pageReleases);
                }
                catch (Exception ex)
                {
                    logger.Warn($"GranTorrent: Error fetching homepage item {href}: {ex.Message}");
                }
            }

            return releases;
        }

        public override async Task<byte[]> Download(Uri link)
        {
            var linkStr = link.ToString();

            // If the link is a direct torrent URL (from data-src), download directly
            if (linkStr.Contains(".torrent"))
            {
                var torrentResult = await RequestWithCookiesAndRetryAsync(linkStr, referer: SiteLink);
                if (torrentResult.ContentBytes != null && torrentResult.ContentBytes.Length > 0)
                    return torrentResult.ContentBytes;
            }

            // Fallback: if the link is a super-enlace URL, try to follow it
            if (linkStr.Contains("super-enlace.com"))
            {
                var redirectPage = await RequestWithCookiesAndRetryAsync(linkStr, referer: SiteLink);
                if (redirectPage.Status != HttpStatusCode.OK)
                    throw new Exception("Error fetching download redirect page.");

                var htmlParser = new HtmlParser();
                using var doc = htmlParser.ParseDocument(redirectPage.ContentString);

                // Try to find a direct torrent link on the redirect page
                var torrentLink = doc.QuerySelector("a[href*='.torrent']")?.GetAttribute("href");
                if (torrentLink.IsNotNullOrWhiteSpace())
                {
                    var torrentResult = await RequestWithCookiesAndRetryAsync(torrentLink, referer: linkStr);
                    return torrentResult.ContentBytes;
                }

                // Try to find a meta refresh or JS redirect
                var metaRefresh = doc.QuerySelector("meta[http-equiv='refresh']")?.GetAttribute("content");
                if (metaRefresh.IsNotNullOrWhiteSpace())
                {
                    var urlMatch = Regex.Match(metaRefresh, @"url=(.+)", RegexOptions.IgnoreCase);
                    if (urlMatch.Success)
                    {
                        var redirectUrl = urlMatch.Groups[1].Value.Trim();
                        var torrentResult = await RequestWithCookiesAndRetryAsync(redirectUrl, referer: linkStr);
                        return torrentResult.ContentBytes;
                    }
                }

                throw new Exception("Could not resolve torrent download from super-enlace redirect.");
            }

            // Generic download attempt
            var genericResult = await RequestWithCookiesAndRetryAsync(linkStr, referer: SiteLink);
            return genericResult.ContentBytes;
        }

        private static TorznabQuery SanitizeTorznabQuery(TorznabQuery query)
        {
            var searchTerm = query.SanitizedSearchTerm;

            // Replace punctuation symbols with spaces
            searchTerm = Regex.Replace(searchTerm, @"[-._\(\)@/\\\[\]\+\%]", " ");
            searchTerm = searchTerm.Trim();

            // Parse the year and remove it from search
            var r = new Regex("([ ]+([0-9]{4}))$", RegexOptions.IgnoreCase);
            var m = r.Match(searchTerm);
            if (m.Success)
            {
                query.Year = int.Parse(m.Groups[2].Value);
                searchTerm = searchTerm.Replace(m.Groups[1].Value, "");
            }

            // Remove some words that do not help with search on Spanish sites
            searchTerm = Regex.Replace(searchTerm, @"\b(espa[ñn]ol|spanish|castellano|spa)\b", "", RegexOptions.IgnoreCase);
            searchTerm = searchTerm.Trim();

            query.SearchTerm = searchTerm;
            return query;
        }

        private string BuildReleaseTitle(string title, bool isSeries, string episodeInfo, string quality, string formato)
        {
            var releaseTitle = title.Trim();

            if (isSeries && episodeInfo.IsNotNullOrWhiteSpace())
            {
                // Parse episode info like "2x1" or "1x01" into S02E01 format
                var match = Regex.Match(episodeInfo, @"(\d+)x(\d+)");
                if (match.Success)
                {
                    var s = match.Groups[1].Value.PadLeft(2, '0');
                    var e = match.Groups[2].Value.PadLeft(2, '0');
                    releaseTitle += $" S{s}E{e}";
                }
            }

            if (quality.IsNotNullOrWhiteSpace())
            {
                releaseTitle += $" [{quality}]";
            }
            else if (isSeries)
            {
                releaseTitle += " [HDTV]";
            }

            if (formato.IsNotNullOrWhiteSpace())
            {
                releaseTitle += $" [{formato}]";
            }

            releaseTitle += " SPANISH";

            return releaseTitle;
        }

        private string DetermineCategory(bool isSeries, string quality, string episodeInfo, long size)
        {
            if (isSeries)
            {
                if (quality.IsNotNullOrWhiteSpace())
                {
                    if (quality.Contains("720"))
                        return GranTorrentCatType.Serie720;
                    if (quality.Contains("1080"))
                        return GranTorrentCatType.Serie1080;
                    if (quality.ToLower().Contains("4k") || quality.ToLower().Contains("2160"))
                        return GranTorrentCatType.Serie4K;
                }

                // Estimate quality from file size for series episodes
                if (size > 4.Gigabytes())
                    return GranTorrentCatType.Serie4K;
                if (size > 1500.Megabytes())
                    return GranTorrentCatType.Serie1080;
                if (size > 800.Megabytes())
                    return GranTorrentCatType.Serie720;

                return GranTorrentCatType.Serie;
            }
            else
            {
                if (quality.IsNotNullOrWhiteSpace())
                {
                    if (quality.Contains("720"))
                        return GranTorrentCatType.Pelicula720;
                    if (quality.Contains("1080"))
                        return GranTorrentCatType.Pelicula1080;
                    if (quality.ToLower().Contains("4k") || quality.ToLower().Contains("2160"))
                        return GranTorrentCatType.Pelicula4K;
                }

                // Estimate quality from file size for movies
                if (size > 20.Gigabytes())
                    return GranTorrentCatType.Pelicula4K;
                if (size > 8.Gigabytes())
                    return GranTorrentCatType.Pelicula1080;
                if (size > 3.Gigabytes())
                    return GranTorrentCatType.Pelicula720;

                return GranTorrentCatType.Pelicula;
            }
        }

        private void ParseEpisodeInfo(string episodeInfo, out int? season, out int? episode)
        {
            season = null;
            episode = null;

            var match = Regex.Match(episodeInfo, @"(\d+)x(\d+)");
            if (match.Success)
            {
                season = int.Parse(match.Groups[1].Value);
                episode = int.Parse(match.Groups[2].Value);
            }
        }

        private long ParseSize(string sizeText)
        {
            if (sizeText.IsNullOrWhiteSpace())
                return 0;

            // Clean up the text: remove trailing dots, spaces
            sizeText = sizeText.Trim().TrimEnd('.').Trim();

            // Try to parse formats like "0.33GB", "900 mb", "3.25GB.", "1.5 GB"
            var match = Regex.Match(sizeText, @"([\d.,]+)\s*(gb|mb|tb|kb)", RegexOptions.IgnoreCase);
            if (!match.Success)
                return 0;

            var numberStr = match.Groups[1].Value.Replace(",", ".");
            if (!double.TryParse(numberStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var number))
                return 0;

            var unit = match.Groups[2].Value.ToLower();
            return unit switch
            {
                "tb" => (long)(number * 1024 * 1024 * 1024 * 1024),
                "gb" => (long)(number * 1024 * 1024 * 1024),
                "mb" => (long)(number * 1024 * 1024),
                "kb" => (long)(number * 1024),
                _ => 0
            };
        }
    }

    internal static class GranTorrentCatType
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
