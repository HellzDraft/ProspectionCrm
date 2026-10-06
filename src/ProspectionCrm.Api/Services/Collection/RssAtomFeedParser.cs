using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using ProspectionCrm.Api.Dtos.Ingestions;

namespace ProspectionCrm.Api.Services.Collection;

public sealed class RssAtomFeedParser
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Content = "http://purl.org/rss/1.0/modules/content/";
    private static readonly Regex Tags = new("<[^>]*>", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public SourceAdapterResult Parse(RssFeedResponse feed, RssCriteria criteria, int maxResponseBytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (feed.Content.Length > maxResponseBytes) throw Error("ResponseTooLarge");
        XDocument document;
        try
        {
            using var stream = new MemoryStream(feed.Content, writable: false);
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = maxResponseBytes, MaxCharactersFromEntities = maxResponseBytes
            };
            // Bound nesting before constructing an XElement graph or formatting XHTML nodes.
            using (var guard = XmlReader.Create(stream, settings))
                while (guard.Read())
                {
                    token.ThrowIfCancellationRequested();
                    if (guard.Depth > 64) throw Error("InvalidFeed");
                }
            stream.Position = 0;
            using var reader = XmlReader.Create(stream, settings);
            document = XDocument.Load(reader);
        }
        catch (XmlException) { throw Error("InvalidFeed"); }
        token.ThrowIfCancellationRequested();
        var root = document.Root;
        var rss = root?.Name == "rss" && (string?)root.Attribute("version") == "2.0" && root.Element("channel") is not null;
        if (!rss && root?.Name != Atom + "feed") throw Error("InvalidFeed");
        var entries = rss ? root!.Element("channel")!.Elements("item") : root!.Elements(Atom + "entry");
        var items = new List<IngestionItemRequest>();
        var read = 0; var skipped = 0; var usable = 0;
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            read++;
            var title = Text(entry.Element(rss ? "title" : Atom + "title"), 200);
            var identity = entry.Element(rss ? "guid" : Atom + "id")?.Value.Trim();
            if (string.IsNullOrWhiteSpace(identity)) identity = null;
            string? url;
            if (rss)
            {
                url = Link(entry.Element("link")?.Value, feed.FinalUri);
                var guid = entry.Element("guid");
                // RSS 2.0 specifies isPermaLink=true by default; opaque/relative GUIDs are never URLs.
                if (url is null && guid is not null && ((string?)guid.Attribute("isPermaLink") is null or "true")
                    && Uri.TryCreate(identity, UriKind.Absolute, out _)) url = Link(identity, feed.FinalUri);
            }
            else
            {
                var links = entry.Elements(Atom + "link").ToArray();
                url = links.Where(l => (string?)l.Attribute("rel") is null or "alternate")
                    .Select(l => Link((string?)l.Attribute("href"), feed.FinalUri)).FirstOrDefault(l => l is not null)
                    ?? links.Select(l => Link((string?)l.Attribute("href"), feed.FinalUri)).FirstOrDefault(l => l is not null);
            }
            if (identity?.Length > 500)
                identity = url is not null ? null : "rss-sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
            if (title is null || (identity is null && url is null)) { skipped++; continue; }
            usable++;
            if (items.Count >= criteria.MaxItems) continue;
            var description = rss ? entry.Element(Content + "encoded") ?? entry.Element("description")
                : entry.Element(Atom + "content") ?? entry.Element(Atom + "summary");
            items.Add(new IngestionItemRequest
            {
                Title = title, ExternalId = identity, SourceUrl = url,
                CompanyName = criteria.DefaultCompanyName, Location = null, Description = Text(description, 10000)
            });
        }
        return new(items, new("rss", rss ? "rss2" : "atom1", read, items.Count, skipped, usable > criteria.MaxItems));
    }
    private static string? Text(XElement? element, int limit)
    {
        if (element is null) return null;
        var raw = element.HasElements ? string.Concat(element.Nodes().Select(n => n.ToString(SaveOptions.DisableFormatting))) : element.Value;
        var text = Spaces.Replace(Tags.Replace(WebUtility.HtmlDecode(raw), " "), " ").Trim();
        if (text.Length == 0) return null;
        // Do not split a UTF-16 surrogate pair at the deterministic size boundary.
        if (text.Length > limit) text = text[..(char.IsHighSurrogate(text[limit - 1]) ? limit - 1 : limit)];
        return text;
    }
    private static string? Link(string? value, Uri baseUri)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value) || value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == '\\')
            || !Uri.TryCreate(baseUri, value, out var uri) || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length != 0 || uri.AbsoluteUri.Length > 2048) return null;
        return IngestionNormalization.UrlKey(uri.AbsoluteUri) is null ? null : uri.AbsoluteUri;
    }
    private static SourceCollectionException Error(string code) => new(new(code, 422));
}
