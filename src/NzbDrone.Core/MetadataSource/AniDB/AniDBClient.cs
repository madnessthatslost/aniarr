using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.MetadataSource.AniDB
{
    public interface IAniDBClient
    {
        Tuple<Series, List<Episode>> GetSeriesInfo(int aniDbId);
        List<Series> SearchByTitle(string title);
        List<Series> SearchById(int aniDbId);
    }

    public class AniDBClient : IAniDBClient
    {
        private const string AniDbHttpApiBase = "http://api.anidb.net:9001/httpapi";
        private const string AniDbClientName = "aniarr";
        private const int AniDbClientVersion = 1;
        private const int AniDbProtocolVersion = 1;
        private const string AniDbImageBase = "https://cdn.anidb.net/images/main/";
        private const string AniDbTitlesDumpUrl = "https://anidb.net/api/anime-titles.xml.gz";

        // AniDB resource cross-reference type numbers
        private const int ResourceTypeMyAnimeList = 4;
        private const int ResourceTypeAniList = 7;
        private const int ResourceTypeTvdb = 12;

        private static readonly XNamespace XmlNs = "http://www.w3.org/XML/1998/namespace";

        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;

        private static List<(int AniDbId, List<(string Lang, string Type, string Value)> Titles)> _titlesCache;
        private static DateTime _titlesCacheExpiry = DateTime.MinValue;
        private static readonly SemaphoreSlim _titlesCacheLock = new SemaphoreSlim(1, 1);

        // AniDB HTTP API rate limit: 1 request per 2 seconds
        private static DateTime _lastApiRequest = DateTime.MinValue;
        private static readonly object _rateLimitLock = new object();

        public AniDBClient(IHttpClient httpClient, Logger logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public Tuple<Series, List<Episode>> GetSeriesInfo(int aniDbId)
        {
            var doc = FetchAnimeXml(aniDbId);
            var series = MapSeries(doc);
            var episodes = doc.Descendants("episode").Select(MapEpisode).Where(e => e != null).ToList();
            return new Tuple<Series, List<Episode>>(series, episodes);
        }

        public List<Series> SearchByTitle(string title)
        {
            var entries = GetTitlesCache();
            if (entries == null || entries.Count == 0)
            {
                _logger.Warn("AniDB titles cache is unavailable; cannot search by title");
                return new List<Series>();
            }

            var lower = title.ToLowerInvariant();

            var matches = entries
                .Where(e => e.Titles.Any(t => t.Value != null && t.Value.ToLowerInvariant().Contains(lower)))
                .Take(20);

            var results = new List<Series>();
            foreach (var (aniDbId, titles) in matches)
            {
                var stub = new Series
                {
                    AniDbId = aniDbId,
                    OriginalLanguage = Language.Japanese,
                    SeriesType = SeriesTypes.Anime,
                    Monitored = true
                };

                stub.Title = PickTitle(titles, "en", "official")
                    ?? PickTitle(titles, "x-jat", "main")
                    ?? titles.FirstOrDefault().Value
                    ?? $"AniDB #{aniDbId}";

                stub.CleanTitle = stub.Title.CleanSeriesTitle();
                stub.SortTitle = SeriesTitleNormalizer.Normalize(stub.Title, aniDbId);
                stub.TitleSlug = $"anidb-{aniDbId}";

                results.Add(stub);
            }

            return results;
        }

        public List<Series> SearchById(int aniDbId)
        {
            try
            {
                var (series, _) = GetSeriesInfo(aniDbId);
                return new List<Series> { series };
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "AniDB lookup failed for id {0}", aniDbId);
                return new List<Series>();
            }
        }

        private XDocument FetchAnimeXml(int aniDbId)
        {
            RateLimit();

            var url = $"{AniDbHttpApiBase}?request=anime&client={AniDbClientName}&clientver={AniDbClientVersion}&protover={AniDbProtocolVersion}&aid={aniDbId}";
            _logger.Debug("Fetching AniDB anime id {0}", aniDbId);

            var request = new HttpRequest(url);
            request.AllowAutoRedirect = true;
            request.SuppressHttpError = true;

            var response = _httpClient.Get(request);

            if (response.HasHttpError)
            {
                throw new HttpException(request, response);
            }

            var xml = response.Content;

            if (xml.Contains("<error>") || xml.Contains("Anime not found"))
            {
                throw new Exceptions.SeriesNotFoundException(aniDbId);
            }

            // Strip DOCTYPE which XDocument cannot process
            var cleaned = Regex.Replace(xml, @"<!DOCTYPE[^>]*>", "", RegexOptions.Singleline);

            return XDocument.Parse(cleaned);
        }

        private Series MapSeries(XDocument doc)
        {
            var root = doc.Root;
            var series = new Series();

            series.AniDbId = (int?)root.Attribute("id") ?? 0;
            series.OriginalLanguage = Language.Japanese;
            series.SeriesType = SeriesTypes.Anime;

            // Cross-reference IDs from <resources>
            foreach (var resource in root.Descendants("resource"))
            {
                var type = (int?)resource.Attribute("type") ?? 0;
                var identifier = resource.Descendants("identifier").FirstOrDefault()?.Value;

                if (identifier == null)
                {
                    continue;
                }

                switch (type)
                {
                    case ResourceTypeTvdb when int.TryParse(identifier, out var tvdbId):
                        series.TvdbId = tvdbId;
                        break;
                    case ResourceTypeMyAnimeList when int.TryParse(identifier, out var malId):
                        series.MalIds = new HashSet<int> { malId };
                        break;
                    case ResourceTypeAniList when int.TryParse(identifier, out var aniListId):
                        series.AniListIds = new HashSet<int> { aniListId };
                        break;
                }
            }

            var rawTitles = ParseTitleElements(root.Element("titles")?.Elements("title"));

            series.Title = PickTitle(rawTitles, "en", "official")
                ?? PickTitle(rawTitles, "x-jat", "main")
                ?? rawTitles.FirstOrDefault().Value
                ?? $"AniDB #{series.AniDbId}";

            series.CleanTitle = series.Title.CleanSeriesTitle();
            series.SortTitle = SeriesTitleNormalizer.Normalize(series.Title, series.AniDbId);
            series.TitleSlug = $"anidb-{series.AniDbId}";
            series.Overview = root.Element("description")?.Value;

            var startDate = root.Element("startdate")?.Value;
            if (startDate.IsNotNullOrWhiteSpace() &&
                DateTime.TryParseExact(startDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var firstAired))
            {
                series.FirstAired = firstAired;
                series.Year = firstAired.Year;
            }

            var endDate = root.Element("enddate")?.Value;
            if (endDate.IsNotNullOrWhiteSpace() &&
                DateTime.TryParseExact(endDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var lastAired))
            {
                series.LastAired = lastAired;
            }

            series.Status = DetermineStatus(series.FirstAired, series.LastAired);

            var permanent = root.Element("ratings")?.Element("permanent");
            if (permanent != null && decimal.TryParse(permanent.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var ratingVal))
            {
                series.Ratings = new Ratings
                {
                    Value = ratingVal,
                    Votes = (int?)permanent.Attribute("votes") ?? 0
                };
            }

            var picture = root.Element("picture")?.Value;
            if (picture.IsNotNullOrWhiteSpace())
            {
                series.Images = new List<MediaCover.MediaCover>
                {
                    new MediaCover.MediaCover
                    {
                        RemoteUrl = AniDbImageBase + picture,
                        CoverType = MediaCoverTypes.Poster
                    }
                };
            }

            series.Seasons = BuildSeasons(doc);
            series.Monitored = true;

            return series;
        }

        private Episode MapEpisode(XElement ep)
        {
            var epNoEl = ep.Element("epno");
            if (epNoEl == null)
            {
                return null;
            }

            var epType = (int?)epNoEl.Attribute("type") ?? 0;
            var epNoValue = epNoEl.Value?.Trim();

            int seasonNumber;
            int episodeNumber;

            if (epType == 1 && int.TryParse(epNoValue, out episodeNumber))
            {
                seasonNumber = 1;
            }
            else if (epType == 2)
            {
                // Specials: epno value is like "S1", "S2"
                var raw = epNoValue?.TrimStart('S', 's');
                if (!int.TryParse(raw, out episodeNumber))
                {
                    return null;
                }

                seasonNumber = 0;
            }
            else
            {
                // Skip credits (3), trailers (4), parodies (5), other (6)
                return null;
            }

            var rawTitles = ParseTitleElements(ep.Elements("title"));
            var title = PickTitle(rawTitles, "en", null)
                ?? PickTitle(rawTitles, "x-jat", null)
                ?? rawTitles.FirstOrDefault().Value
                ?? $"Episode {episodeNumber}";

            var airDateStr = ep.Element("airdate")?.Value;
            string airDate = null;
            DateTime? airDateUtc = null;

            if (airDateStr.IsNotNullOrWhiteSpace() &&
                DateTime.TryParseExact(airDateStr, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedDate))
            {
                airDate = airDateStr;
                airDateUtc = parsedDate;
            }

            var episode = new Episode
            {
                TvdbId = (int?)ep.Attribute("id") ?? 0,
                SeasonNumber = seasonNumber,
                EpisodeNumber = episodeNumber,
                AbsoluteEpisodeNumber = epType == 1 ? episodeNumber : (int?)null,
                Title = title,
                AirDate = airDate,
                AirDateUtc = airDateUtc
            };

            if (int.TryParse(ep.Element("length")?.Value, out var runtime))
            {
                episode.Runtime = runtime;
            }

            var ratingEl = ep.Element("rating");
            if (ratingEl != null && decimal.TryParse(ratingEl.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var ratingVal))
            {
                episode.Ratings = new Ratings
                {
                    Value = ratingVal,
                    Votes = (int?)ratingEl.Attribute("votes") ?? 0
                };
            }

            return episode;
        }

        private static List<Season> BuildSeasons(XDocument doc)
        {
            var episodes = doc.Descendants("episode").ToList();
            var seasons = new List<Season>();

            bool hasSpecial = episodes.Any(e => (int?)e.Element("epno")?.Attribute("type") == 2);
            bool hasRegular = episodes.Any(e => (int?)e.Element("epno")?.Attribute("type") == 1);

            if (hasSpecial)
            {
                seasons.Add(new Season { SeasonNumber = 0, Monitored = false });
            }

            if (hasRegular)
            {
                seasons.Add(new Season { SeasonNumber = 1, Monitored = true });
            }

            return seasons;
        }

        private static SeriesStatusType DetermineStatus(DateTime? firstAired, DateTime? lastAired)
        {
            if (lastAired.HasValue)
            {
                return lastAired.Value < DateTime.UtcNow ? SeriesStatusType.Ended : SeriesStatusType.Continuing;
            }

            if (firstAired.HasValue && firstAired.Value > DateTime.UtcNow)
            {
                return SeriesStatusType.Upcoming;
            }

            return SeriesStatusType.Continuing;
        }

        private static List<(string Lang, string Type, string Value)> ParseTitleElements(IEnumerable<XElement> elements)
        {
            if (elements == null)
            {
                return new List<(string, string, string)>();
            }

            return elements.Select(t => (
                Lang: t.Attribute(XmlNs + "lang")?.Value ?? t.Attribute("lang")?.Value ?? "",
                Type: t.Attribute("type")?.Value ?? "",
                Value: t.Value
            )).ToList();
        }

        private static string PickTitle(List<(string Lang, string Type, string Value)> titles, string lang, string type)
        {
            return titles.FirstOrDefault(t =>
                    string.Equals(t.Lang, lang, StringComparison.OrdinalIgnoreCase) &&
                    (type == null || string.Equals(t.Type, type, StringComparison.OrdinalIgnoreCase)))
                .Value;
        }

        private List<(int AniDbId, List<(string Lang, string Type, string Value)> Titles)> GetTitlesCache()
        {
            if (_titlesCache != null && DateTime.UtcNow < _titlesCacheExpiry)
            {
                return _titlesCache;
            }

            _titlesCacheLock.Wait();
            try
            {
                if (_titlesCache != null && DateTime.UtcNow < _titlesCacheExpiry)
                {
                    return _titlesCache;
                }

                _titlesCache = FetchTitlesDump();
                _titlesCacheExpiry = DateTime.UtcNow.AddHours(24);
                return _titlesCache;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to fetch AniDB titles dump");
                return _titlesCache;
            }
            finally
            {
                _titlesCacheLock.Release();
            }
        }

        private List<(int AniDbId, List<(string Lang, string Type, string Value)> Titles)> FetchTitlesDump()
        {
            _logger.Debug("Fetching AniDB anime titles dump from {0}", AniDbTitlesDumpUrl);

            var request = new HttpRequest(AniDbTitlesDumpUrl);
            request.AllowAutoRedirect = true;
            var response = _httpClient.Get(request);

            using var compressed = new MemoryStream(response.ResponseData);
            using var decompressed = new GZipStream(compressed, CompressionMode.Decompress);
            using var reader = new StreamReader(decompressed, Encoding.UTF8);
            var xml = reader.ReadToEnd();

            var doc = XDocument.Parse(xml);

            return doc.Descendants("anime").Select(a =>
            {
                var aniDbId = (int?)a.Attribute("aid") ?? 0;
                var titles = ParseTitleElements(a.Elements("title"));
                return (aniDbId, titles);
            }).Where(e => e.aniDbId > 0).ToList();
        }

        private static void RateLimit()
        {
            lock (_rateLimitLock)
            {
                var elapsed = DateTime.UtcNow - _lastApiRequest;
                if (elapsed.TotalSeconds < 2)
                {
                    Thread.Sleep(TimeSpan.FromSeconds(2) - elapsed);
                }

                _lastApiRequest = DateTime.UtcNow;
            }
        }
    }
}
