using System.Xml.Linq;
using MyDM.Core.Models;

namespace MyDM.Core.Parsers;

public static class DashParser
{
    /// <summary>
    /// Parse a DASH MPD manifest and extract available representations with quality info.
    /// </summary>
    public static List<MediaQuality> ParseMpd(string content, string baseUrl)
    {
        var qualities = new List<MediaQuality>();

        try
        {
            var doc = XDocument.Parse(content);
            var ns = doc.Root?.Name.Namespace ?? XNamespace.None;

            var periods = doc.Root?.Elements(ns + "Period") ?? Enumerable.Empty<XElement>();
            foreach (var period in periods)
            {
                var adaptationSets = period.Elements(ns + "AdaptationSet");
                foreach (var adaptSet in adaptationSets)
                {
                    var mimeType = adaptSet.Attribute("mimeType")?.Value ?? "";
                    var isVideo = mimeType.Contains("video") ||
                                  adaptSet.Attribute("contentType")?.Value == "video";

                    if (!isVideo) continue;

                    var representations = adaptSet.Elements(ns + "Representation");
                    foreach (var rep in representations)
                    {
                        var bandwidth = rep.Attribute("bandwidth")?.Value;
                        var width = rep.Attribute("width")?.Value;
                        var height = rep.Attribute("height")?.Value;
                        var codecs = rep.Attribute("codecs")?.Value ??
                                     adaptSet.Attribute("codecs")?.Value;
                        var id = rep.Attribute("id")?.Value ?? "";

                        var resolution = width != null && height != null
                            ? $"{width}x{height}"
                            : "Unknown";

                        // Try to get the base URL for this representation
                        var repBaseUrl = rep.Element(ns + "BaseURL")?.Value ??
                                         adaptSet.Element(ns + "BaseURL")?.Value ??
                                         period.Element(ns + "BaseURL")?.Value ??
                                         doc.Root?.Element(ns + "BaseURL")?.Value;

                        var url = repBaseUrl != null ? ResolveUrl(repBaseUrl, baseUrl) : baseUrl;

                        qualities.Add(new MediaQuality
                        {
                            Resolution = resolution,
                            Bandwidth = long.TryParse(bandwidth, out var bw) ? bw : 0,
                            Codecs = codecs,
                            Url = url
                        });
                    }
                }
            }
        }
        catch (Exception)
        {
            // Return empty list on parse failure
        }

        return qualities.OrderByDescending(q => q.Bandwidth).ToList();
    }

    /// <summary>
    /// Extract segment URLs from a DASH representation using SegmentTemplate or SegmentList.
    /// </summary>
    public static List<string> ExtractSegmentUrls(string content, string baseUrl, string? representationId = null)
    {
        var segments = new List<string>();

        try
        {
            var doc = XDocument.Parse(content);
            var ns = doc.Root?.Name.Namespace ?? XNamespace.None;

            var periods = doc.Root?.Elements(ns + "Period") ?? Enumerable.Empty<XElement>();
            foreach (var period in periods)
            {
                foreach (var adaptSet in period.Elements(ns + "AdaptationSet"))
                {
                    foreach (var rep in adaptSet.Elements(ns + "Representation"))
                    {
                        if (representationId != null && rep.Attribute("id")?.Value != representationId)
                            continue;

                        // Check for SegmentTemplate
                        var segTemplate = rep.Element(ns + "SegmentTemplate") ??
                                          adaptSet.Element(ns + "SegmentTemplate");
                        if (segTemplate != null)
                        {
                            var init = segTemplate.Attribute("initialization")?.Value;
                            var media = segTemplate.Attribute("media")?.Value;
                            var startNumber = int.Parse(segTemplate.Attribute("startNumber")?.Value ?? "1");
                            var timescale = int.Parse(segTemplate.Attribute("timescale")?.Value ?? "1");

                            var timeline = segTemplate.Element(ns + "SegmentTimeline");
                            if (timeline != null)
                            {
                                if (init != null)
                                {
                                    var initUrl = init.Replace("$RepresentationID$", rep.Attribute("id")?.Value ?? "");
                                    segments.Add(ResolveUrl(initUrl, baseUrl));
                                }

                                int number = startNumber;
                                long time = 0;
                                foreach (var s in timeline.Elements(ns + "S"))
                                {
                                    var t = s.Attribute("t")?.Value;
                                    var d = long.Parse(s.Attribute("d")?.Value ?? "0");
                                    var r = int.Parse(s.Attribute("r")?.Value ?? "0");

                                    if (t != null) time = long.Parse(t);

                                    for (int i = 0; i <= r; i++)
                                    {
                                        if (media != null)
                                        {
                                            var segUrl = media
                                                .Replace("$RepresentationID$", rep.Attribute("id")?.Value ?? "")
                                                .Replace("$Number$", number.ToString())
                                                .Replace("$Time$", time.ToString());
                                            segments.Add(ResolveUrl(segUrl, baseUrl));
                                        }
                                        number++;
                                        time += d;
                                    }
                                }
                            }
                        }

                        // Check for SegmentList
                        var segList = rep.Element(ns + "SegmentList");
                        if (segList != null)
                        {
                            var initEl = segList.Element(ns + "Initialization");
                            if (initEl != null)
                            {
                                var sourceUrl = initEl.Attribute("sourceURL")?.Value;
                                if (sourceUrl != null)
                                    segments.Add(ResolveUrl(sourceUrl, baseUrl));
                            }

                            foreach (var segUrl in segList.Elements(ns + "SegmentURL"))
                            {
                                var mediaUrl = segUrl.Attribute("media")?.Value;
                                if (mediaUrl != null)
                                    segments.Add(ResolveUrl(mediaUrl, baseUrl));
                            }
                        }

                        if (representationId != null) return segments;
                    }
                }
            }
        }
        catch { /* Return what we have */ }

        return segments;
    }

    /// <summary>
    /// Check if content looks like a DASH MPD manifest.
    /// </summary>
    public static bool IsMpd(string content)
    {
        return content.Contains("<MPD") || content.Contains("urn:mpeg:dash:schema:mpd");
    }

    private static string ResolveUrl(string url, string baseUrl)
    {
        if (Uri.IsWellFormedUriString(url, UriKind.Absolute))
            return url;

        try
        {
            var baseUri = new Uri(baseUrl);
            return new Uri(baseUri, url).AbsoluteUri;
        }
        catch
        {
            return url;
        }
    }
}
