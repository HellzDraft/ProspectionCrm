using System.Text;
using ProspectionCrm.Api.Services.Collection;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class RssAtomFeedParserTests
{
    private static SourceAdapterResult Parse(string xml, int max = 100, string? company = null, int bytes = 2_000_000) =>
        new RssAtomFeedParser().Parse(new(Encoding.UTF8.GetBytes(xml), new("https://feeds.example.org/path/feed.xml")), new(max, company), bytes, default);
    public static string Rss(string items) => "<rss version='2.0' xmlns:content='http://purl.org/rss/1.0/modules/content/' xmlns:x='urn:extra'><channel>" + items + "</channel></rss>";
    public static string Atom(string entries) => "<feed xmlns='http://www.w3.org/2005/Atom'>" + entries + "</feed>";

    [Theory]
    [InlineData("<guid>opaque</guid><link>https://jobs.example.org/one</link>", "opaque", "https://jobs.example.org/one")]
    [InlineData("<link>https://jobs.example.org/one</link>", null, "https://jobs.example.org/one")]
    [InlineData("<guid isPermaLink='false'>OpaqueID</guid>", "OpaqueID", null)]
    [InlineData("<guid>opaque</guid>", "opaque", null)]
    [InlineData("<guid isPermaLink='true'>https://jobs.example.org/one</guid>", "https://jobs.example.org/one", "https://jobs.example.org/one")]
    [InlineData("<guid>https://jobs.example.org/default</guid>", "https://jobs.example.org/default", "https://jobs.example.org/default")]
    [InlineData("<guid isPermaLink='false'>https://jobs.example.org/one</guid>", "https://jobs.example.org/one", null)]
    [InlineData("<guid> x </guid><link>../jobs/one</link>", "x", "https://feeds.example.org/jobs/one")]
    public void RssMapsPersistentIdentity(string identity, string? external, string? url)
    {
        var result = Parse(Rss("<item><title>Job</title>" + identity + "</item>"));
        var item = Assert.Single(result.Items);
        Assert.Equal(external, item.ExternalId); Assert.Equal(url, item.SourceUrl);
        Assert.Equal("rss2", result.Summary.FeedFormat); Assert.Equal(1, result.Summary.EntriesRead);
    }

    [Fact]
    public void RssMapsHtmlPriorityAndIgnoresUnmappedMetadata()
    {
        var item = Assert.Single(Parse(Rss("""
            <item><title><![CDATA[ <b>Developer</b> &amp;   engineer ]]></title><guid>CaseSensitive</guid>
            <description>fallback</description><content:encoded><![CDATA[<p>One&nbsp; &amp; two</p><p>Next</p>]]></content:encoded>
            <author>private@example.org</author><pubDate>yesterday</pubDate><category>Secret</category><x:field>extra</x:field></item>
            """), company: "Employer").Items);
        Assert.Equal("Developer & engineer", item.Title); Assert.Equal("One & two Next", item.Description);
        Assert.Equal("Employer", item.CompanyName); Assert.Null(item.Location);
        Assert.Equal("Fallback", Assert.Single(Parse(Rss("<item><title>Job</title><guid>a</guid><description> Fallback </description></item>")).Items).Description);
    }

    [Fact]
    public void CountsEntireDocumentTruncatesOnlyUsableItemsAndPreservesDuplicates()
    {
        const string good = "<item><title>Job</title><guid>a</guid></item>";
        var result = Parse(Rss("<item><title> </title><guid>bad</guid></item>" + good + "<item><title>No identity</title></item>" + good + good), 2);
        Assert.Equal(5, result.Summary.EntriesRead); Assert.Equal(2, result.Summary.EntriesSkipped);
        Assert.Equal(2, result.Summary.EntriesMapped); Assert.True(result.Summary.WasTruncated);
        Assert.Equal(new[] { "a", "a" }, result.Items.Select(x => x.ExternalId));
        Assert.False(Parse(Rss(good + good), 2).Summary.WasTruncated);
        Assert.Empty(Parse(Rss("<item><title>No identity</title></item>")).Items);
    }

    [Fact]
    public void SizeBoundariesAndLongOpaqueIdentitiesAreDeterministic()
    {
        var id = new string('é', 501); var title = new string('t', 201); var description = new string('d', 10001);
        var xml = Rss($"<item><title>{title}</title><guid>{id}</guid><description>{description}</description></item>");
        var item = Assert.Single(Parse(xml).Items);
        Assert.Equal(200, item.Title.Length); Assert.Equal(10000, item.Description!.Length);
        Assert.Matches("^rss-sha256:[0-9a-f]{64}$", item.ExternalId!);
        Assert.Equal(item.ExternalId, Assert.Single(Parse(xml).Items).ExternalId);
        Assert.Null(Assert.Single(Parse(Rss($"<item><title>Job</title><guid>{id}</guid><link>/one</link></item>")).Items).ExternalId);
        var longUrl = "https://example.org/" + new string('x', 2048);
        Assert.Null(Assert.Single(Parse(Rss($"<item><title>Job</title><guid>a</guid><link>{longUrl}</link></item>")).Items).SourceUrl);
        Assert.Empty(Parse(Rss($"<item><title>Job</title><link>{longUrl}</link></item>")).Items);
        Assert.Equal(500, Assert.Single(Parse(Rss($"<item><title>Job</title><guid>{new string('x', 500)}</guid></item>")).Items).ExternalId!.Length);
        var unicode = Assert.Single(Parse(Rss($"<item><title>{new string('a', 199)}😀end</title><guid>a</guid></item>")).Items);
        Assert.Equal(199, unicode.Title.Length);
    }

    [Theory]
    [InlineData("<link href='/first' rel='self'/><link href='/preferred' rel='alternate'/>", "https://feeds.example.org/preferred")]
    [InlineData("<link href='/first' rel='self'/><link href='ftp://bad.example.org' rel='alternate'/>", "https://feeds.example.org/first")]
    [InlineData("<link href='ftp://bad.example.org'/><link href='../fallback' rel='related'/>", "https://feeds.example.org/fallback")]
    [InlineData("<link href='/implicit'/>", "https://feeds.example.org/implicit")]
    public void AtomSelectsFirstUsableAlternateThenFirstUsableLink(string links, string url)
    {
        var result = Parse(Atom("<entry><title>Job</title><id> ID </id>" + links + "</entry>"));
        Assert.Equal("atom1", result.Summary.FeedFormat);
        var item = Assert.Single(result.Items); Assert.Equal("ID", item.ExternalId); Assert.Equal(url, item.SourceUrl);
    }

    [Theory]
    [InlineData("<content type='text'>Plain &amp; simple</content>", "Plain & simple")]
    [InlineData("<content type='html'>&lt;b&gt;HTML&lt;/b&gt; &amp;amp; text</content>", "HTML & text")]
    [InlineData("<content type='xhtml'><div xmlns='http://www.w3.org/1999/xhtml'><p>First</p><p>Second</p></div></content>", "First Second")]
    [InlineData("<summary>Summary</summary>", "Summary")]
    [InlineData("<content>Content</content><summary>Summary</summary>", "Content")]
    public void AtomMapsContentAndSummary(string content, string expected)
    {
        var item = Assert.Single(Parse(Atom("<entry><title>Job</title><id>a</id>" + content + "</entry>")).Items);
        Assert.Equal(expected, item.Description); Assert.Null(item.CompanyName);
    }

    [Theory]
    [InlineData("ftp://example.org/one")]
    [InlineData("https://user:password@example.org/one")]
    [InlineData("https://example.org/a b")]
    [InlineData("javascript:alert(1)")]
    public void EntryUrlsAreValidatedButNeverFetched(string url)
    {
        Assert.Null(Assert.Single(Parse(Rss($"<item><title>Job</title><guid>a</guid><link>{url}</link></item>")).Items).SourceUrl);
    }

    [Theory]
    [InlineData("<rss>")]
    [InlineData("<rss version='2.0'/>")]
    [InlineData("<rss version='1.0'><channel/></rss>")]
    [InlineData("<html><body/></html>")]
    [InlineData("<feed/>")]
    [InlineData("<entry xmlns='http://www.w3.org/2005/Atom'/>")]
    [InlineData("<!DOCTYPE rss><rss version='2.0'><channel/></rss>")]
    [InlineData("<!DOCTYPE rss [<!ENTITY x SYSTEM 'file:///etc/passwd'>]><rss version='2.0'><channel>&x;</channel></rss>")]
    [InlineData("<!DOCTYPE rss [<!ENTITY a '1234567890'><!ENTITY b '&a;&a;&a;&a;'><!ENTITY c '&b;&b;&b;&b;'>]><rss version='2.0'><channel>&c;</channel></rss>")]
    public void RejectsMalformedUnsupportedAndEntityDocuments(string xml) =>
        Assert.Equal("InvalidFeed", Assert.Throws<SourceCollectionException>(() => Parse(xml)).Error.Code);

    [Fact]
    public void EnforcesBytesAndCancellation()
    {
        Assert.Equal("InvalidFeed", Assert.Throws<SourceCollectionException>(() => Parse(Rss(
            "<item><title>Job</title><guid>a</guid><description>" + string.Concat(Enumerable.Repeat("<x>", 65))
            + "text" + string.Concat(Enumerable.Repeat("</x>", 65)) + "</description></item>"))).Error.Code);
        Assert.Equal("ResponseTooLarge", Assert.Throws<SourceCollectionException>(() => Parse(Rss(""), bytes: 10)).Error.Code);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => new RssAtomFeedParser().Parse(new([], new("https://example.org")), new(100, null), 100, cancelled.Token));
        Assert.Empty(Parse(Rss("")).Items); Assert.Empty(Parse(Atom("")).Items);
    }

    [Theory]
    [InlineData("[]")][InlineData("null")][InlineData("{")][InlineData("{\"unknown\":1}")]
    [InlineData("{\"maxItems\":0}")][InlineData("{\"maxItems\":101}")][InlineData("{\"maxItems\":1.5}")]
    [InlineData("{\"maxItems\":\"1\"}")][InlineData("{\"defaultCompanyName\":null}")]
    [InlineData("{\"defaultCompanyName\":\"  \"}")][InlineData("{\"maxItems\":1,\"maxItems\":2}")]
    public void CriteriaAreStrict(string json) => Assert.Equal("InvalidRssCriteria", Assert.Throws<SourceCollectionException>(() => RssCriteria.Parse(json, 100)).Error.Code);

    [Fact]
    public void CriteriaDefaultsAndTrim()
    {
        Assert.Equal(new RssCriteria(100, null), RssCriteria.Parse("{}", 100));
        Assert.Equal(new RssCriteria(1, "Employer"), RssCriteria.Parse("{\"maxItems\":1,\"defaultCompanyName\":\" Employer \"}", 100));
        Assert.Throws<SourceCollectionException>(() => RssCriteria.Parse("{\"defaultCompanyName\":\"" + new string('c', 201) + "\"}", 100));
    }
}
