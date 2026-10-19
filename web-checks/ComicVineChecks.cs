using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using PanelHaven.Web;

internal static class ComicVineChecks
{
    public static async Task RunAsync()
    {
        const string key = "check-secret-never-return";
        const string volumeUrl = "https://comicvine.gamespot.com/example/4050-12/";
        const string issueUrl = "https://comicvine.gamespot.com/example-1/4000-34/";
        using (var missing = new ComicVine(null))
        {
            Check(!missing.Configured, "Missing key appeared configured.");
            await Error(() => missing.VolumesAsync("Example", 0), 503);
        }
        var handler = new Handler(request =>
        {
            Check(request.RequestUri!.Scheme == "https" && request.RequestUri.Host == "comicvine.gamespot.com"
                && request.RequestUri.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal), "Provider request escaped its fixed origin.");
            Check(request.Headers.UserAgent.Count != 0, "Provider User-Agent is missing.");
            var query = Uri.UnescapeDataString(request.RequestUri.Query);
            Check(query.Contains("api_key=" + key) && query.Contains("format=json"), "Provider authentication or JSON format is missing.");
            switch (request.RequestUri.AbsolutePath)
            {
                case "/api/search/":
                    Check(query.Contains("resources=volume") && query.Contains("query=Example & Friends") && query.Contains("limit=10") && query.Contains("offset=10"), "Volume search encoding/paging failed.");
                    return Json(new { status_code = 1, number_of_total_results = 11, results = new[] {
                        new { id = 12, name = "Example &amp; Friends", start_year = "1999", publisher = new { name = "Publisher" }, site_detail_url = volumeUrl } } });
                case "/api/issues/":
                    Check(query.Contains("filter=volume:12,issue_number:1/2") && query.Contains("limit=20") && query.Contains("offset=20"), "Issue filtering/paging failed.");
                    return Json(new { status_code = 1, number_of_total_results = 21, results = new[] {
                        new { id = 34, name = (string?)null, issue_number = "1/2", cover_date = "2001-02-03", site_detail_url = issueUrl } } });
                case "/api/issue/4000-34/":
                    return Json(new { status_code = 1, results = new {
                        id = 34, volume = new { id = 12 }, name = "<b>A title</b>", issue_number = "1/2", cover_date = "2001-02-03",
                        description = "<p>Hello &amp; welcome</p><script>doNotImport()</script><p>Second\u0001 paragraph 😀</p>",
                        site_detail_url = issueUrl + "?tracking=ignored#fragment", person_credits = new[] {
                            new { name = "Creator", role = "writer, penciler, cover" }, new { name = "Creator", role = "writer" },
                            new { name = "Color Person", role = "colorist, editor" }, new { name = "Ignored", role = "other" } },
                        character_credits = new[] { new { name = "Hero" }, new { name = "Hero" } },
                        story_arc_credits = new[] { new { name = "Story" } }
                    } });
                case "/api/volume/4050-12/":
                    return Json(new { status_code = 1, results = new { id = 12, name = "Example", publisher = new { name = "Publisher" } } });
                default: throw new Exception("Unexpected provider resource.");
            }
        });
        using (var provider = new ComicVine(key, handler))
        {
            Check(provider.Configured, "Configured key was not recognized.");
            var volumes = await provider.VolumesAsync(" Example & Friends ", 10);
            Check(volumes.Limit == 10 && volumes.Offset == 10 && volumes.Total == 11 && volumes.Results.Single() ==
                new ComicVineVolume(12, "Example & Friends", "1999", "Publisher", volumeUrl), "Volume results changed or lost fields.");
            await provider.VolumesAsync("Example & Friends", 10);
            Check(handler.Calls == 1, "Repeated search missed the cache.");
            var issues = await provider.IssuesAsync(12, "1/2", 20);
            Check(issues.Limit == 20 && issues.Offset == 20 && issues.Total == 21 && issues.Results.Single().Name == ""
                && issues.Results[0].Number == "1/2", "Issue results lost optional fields or paging.");
            var metadata = await provider.MetadataAsync(34, 12);
            var fields = metadata.Fields;
            Check(metadata.Url == issueUrl && fields["Web"] == issueUrl && fields["Series"] == "Example" && fields["Publisher"] == "Publisher"
                && fields["Title"] == "A title" && fields["Number"] == "1/2" && fields["Year"] == "2001" && fields["Month"] == "2" && fields["Day"] == "3"
                && fields["Writer"] == "Creator" && fields["Penciller"] == "Creator" && fields["CoverArtist"] == "Creator"
                && fields["Colorist"] == "Color Person" && fields["Editor"] == "Color Person" && fields["Characters"] == "Hero" && fields["StoryArc"] == "Story",
                "Metadata mapping or credit deduplication failed.");
            Check(fields["Summary"] == "Hello & welcome Second paragraph 😀" && !fields.ContainsKey("Genre") && !fields.ContainsKey("Volume")
                && fields.Values.All(value => !string.IsNullOrWhiteSpace(value)) && fields.Keys.All(name => LibraryStore.MetadataFields.Any(field => field.Name == name)),
                "Metadata includes HTML, invalid XML, invented fields, or empty replacements.");
            await provider.MetadataAsync(34, 12);
            Check(handler.Calls == 4, "Metadata cache did not reuse issue and volume responses.");
            Check(handler.Times.Zip(handler.Times.Skip(1)).All(pair => pair.Second - pair.First >= TimeSpan.FromMilliseconds(900)), "Global velocity throttle was bypassed.");
            await Error(() => provider.MetadataAsync(34, 99), 409);
            Check(handler.Calls == 4, "Volume mismatch fetched an unrelated volume.");

            foreach (var query in new[] { "", " ", new string('a', 201), "a\nb" }) await Error(() => provider.VolumesAsync(query, 0), 400);
            foreach (var offset in new[] { -1, 1, 10010 }) await Error(() => provider.VolumesAsync("Example", offset), 400);
            foreach (var number in new[] { "1,volume:9", "1|2", "1%2cvolume", "1&filter=x", "1=2", "1?2", "1#2", "1\n2", new string('1', 41) })
                await Error(() => provider.IssuesAsync(12, number, 0), 400);
            await Error(() => provider.IssuesAsync(0, null, 0), 400);
            await Error(() => provider.IssuesAsync(12, null, 10), 400);
            await Error(() => provider.MetadataAsync(-1, 12), 400);
            Check(handler.Calls == 4, "Invalid input reached Comic Vine.");
        }

        foreach (var url in new[] { "http://comicvine.gamespot.com/a/", "https://evil.example/a/", "https://comicvine.gamespot.com.evil.example/a/",
            "https://user@comicvine.gamespot.com/a/", "https://comicvine.gamespot.com:444/a/", "https://comicvine.gamespot.com/api/issue/4000-34/", "javascript:alert(1)" })
        {
            using var provider = new ComicVine(key, new Handler(_ => Json(new { status_code = 1, number_of_total_results = 1,
                results = new[] { new { id = 12, site_detail_url = url } } })));
            await Error(() => provider.VolumesAsync("Example", 0), 502);
        }
        foreach (var status in new[] { 100, 101, 104, 107 })
        {
            using var provider = new ComicVine(key, new Handler(_ => Json(new { status_code = status, error = key })));
            await Error(() => provider.VolumesAsync("Example", 0), status == 101 ? 404 : status == 107 ? 429 : 502);
        }
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.NotFound, HttpStatusCode.Found, HttpStatusCode.ServiceUnavailable })
        {
            using var provider = new ComicVine(key, new Handler(_ => new HttpResponseMessage(status) { Content = new StringContent(key) }));
            await Error(() => provider.VolumesAsync("Example", 0), status == HttpStatusCode.NotFound ? 404 : 502);
        }
        var limited = new Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent(key) };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(2));
            return response;
        });
        using (var provider = new ComicVine(key, limited))
        {
            await Error(() => provider.VolumesAsync("Example", 0), 429);
            await Error(() => provider.IssuesAsync(12, null, 0), 429);
            Check(limited.Calls == 1, "Rate limit was retried or cooldown was not global.");
        }
        foreach (var body in new[] { key, "{}", "[]", "{\"status_code\":1,\"results\":null}", new string('x', 1024 * 1024 + 1) })
        {
            using var provider = new ComicVine(key, new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }));
            await Error(() => provider.VolumesAsync("Example", 0), 502);
        }
        using (var provider = new ComicVine(key, new Handler(_ => throw new HttpRequestException("https://comicvine.gamespot.com/?api_key=" + key))))
            await Error(() => provider.VolumesAsync("Example", 0), 502);
        using (var provider = new ComicVine(key, new Handler(_ => throw new TaskCanceledException(key))))
            await Error(() => provider.VolumesAsync("Example", 0), 504);
        using (var provider = new ComicVine(key, new Handler(_ => throw new Exception("Cancelled request reached the provider."))))
        {
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try { await provider.VolumesAsync("Example", 0, cancelled.Token); throw new Exception("Cancellation was ignored."); }
            catch (OperationCanceledException exception) { Check(exception.InnerException is null && !exception.ToString().Contains(key), "Cancellation leaked credentials."); }
        }
        using (var provider = new ComicVine(key, new Handler(request => Json(request.RequestUri!.AbsolutePath.Contains("/issue/")
            ? new { status_code = 1, results = (object)new { id = 34, volume = new { id = 12 }, name = (string?)null, description = "<p> </p>", cover_date = "bad", site_detail_url = issueUrl } }
            : new { status_code = 1, results = (object)new { id = 12 } }))))
        {
            var metadata = await provider.MetadataAsync(34, 12);
            Check(metadata.Fields.Count == 1 && metadata.Fields["Web"] == issueUrl, "Absent metadata became empty replacements or invented dates.");
        }
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) };
    private static async Task Error(Func<Task> action, int status)
    {
        try { await action(); }
        catch (ComicVineException exception)
        {
            Check(exception.StatusCode == status && exception.InnerException is null && !exception.ToString().Contains("check-secret-never-return")
                && !exception.Message.Contains("https://"), "Wrong error status or upstream credential/URL leak.");
            return;
        }
        throw new Exception("Expected provider error was not raised.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private readonly Stopwatch elapsed = Stopwatch.StartNew();
        public int Calls { get; private set; }
        public List<TimeSpan> Times { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Times.Add(elapsed.Elapsed);
            return Task.FromResult(respond(request));
        }
    }
}
