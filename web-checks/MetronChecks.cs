using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using PanelHaven.Web;

internal static class MetronChecks
{
    public static async Task RunAsync()
    {
        const string token = "check-secret-never-return";
        const string seriesUrl = "https://metron.cloud/series/12/";
        const string issueUrl = "https://metron.cloud/issue/34/";
        using (var missing = new Metron(null))
        {
            Check(!missing.Configured, "Missing token appeared configured.");
            await Error(() => missing.VolumesAsync("Example", 0), 503);
        }
        foreach (var bad in new[] { "bad\r\nAuthorization: injected", "two tokens", "bad\0token" })
            await Error(() => Task.FromResult(new Metron(bad)), 400);
        var handler = new Handler(request =>
        {
            Check(request.RequestUri!.Scheme == "https" && request.RequestUri.Host == "metron.cloud"
                && request.RequestUri.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal), "Provider request escaped its fixed origin.");
            Check(request.Headers.UserAgent.Count != 0 && request.Headers.Accept.Any(value => value.MediaType == "application/json"), "Provider headers are missing.");
            Check(request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Authorization.Parameter == token
                && !request.RequestUri.ToString().Contains(token), "Token is not confined to the Bearer header.");
            var query = Uri.UnescapeDataString(request.RequestUri.Query);
            switch (request.RequestUri.AbsolutePath)
            {
                case "/api/series/":
                    Check(query.Contains("name=Example & Friends") && query.Contains("page=2")
                        && !query.Contains("offset=") && !query.Contains("limit="), "Series query encoding or actual page mapping failed.");
                    return Json(new { count = 101, next = (string?)null, previous = "https://evil.example/?secret=private", results = new[] {
                        new { id = 12, series = "Example &amp; Friends (1999)", year_began = 1999, volume = 2,
                            publisher = new { id = 1, name = "Publisher" } } } });
                case "/api/issue/":
                    Check(query.Contains("series_id=12") && query.Contains("number=1/2") && query.Contains("page=2"), "Issue filters or pagination failed.");
                    return Json(new { count = 101, next = (string?)null, previous = (string?)null, results = new[] {
                        new { id = 34, series = new { id = 12, name = "Example", volume = 2, year_began = 1999 }, number = "1/2",
                            issue = "Example (1999) #1/2", cover_date = "2001-02-03", store_date = "2001-01-17", image = "https://static.metron.cloud/media/issue/cover.jpg" } } });
                case "/api/issue/34/":
                    return Json(new {
                        id = 34, series = new { id = 12, name = "Example &amp; Friends", year_began = 1999, volume = 2,
                            language = "en", series_type = new { id = 1, name = "Single Issue" }, genres = new[] { new { id = 1, name = "Adventure" } } },
                        publisher = new { id = 1, name = "Publisher" }, imprint = new { id = 2, name = "Imprint" },
                        title = "", name = new[] { "<b>A title</b>", "Another story", "A title" }, number = "1/2", alt_number = "7",
                        page = 32, rating = new { id = 1, name = "Teen" }, cover_date = "2001-02-03", store_date = "2001-01-17",
                        desc = "<p>Hello &amp; welcome</p><script>doNotImport()</script><p>Second\u0001 paragraph 😀</p>",
                        resource_url = "https://evil.example/?token=" + token,
                        attribution = new[] { new { source = "Wikipedia", url = "https://en.wikipedia.org/wiki/Example" } },
                        credits = new[] {
                            new { creator = "Creator", role = new[] { new { id = 1, name = "Writer" }, new { id = 2, name = "Penciller" }, new { id = 3, name = "Cover" } } },
                            new { creator = "Creator", role = new[] { new { id = 1, name = "Writer" } } },
                            new { creator = "Color Person", role = new[] { new { id = 4, name = "Colorist" }, new { id = 5, name = "Editor" } } },
                            new { creator = "Ignored", role = new[] { new { id = 6, name = "Other" } } } },
                        characters = new[] { new { id = 1, name = "Hero" }, new { id = 1, name = "Hero" } },
                        arcs = new[] { new { id = 1, name = "Story" } }, teams = new[] { new { id = 1, name = "Team" } }
                    });
                default: throw new Exception("Unexpected provider resource.");
            }
        });
        using (var provider = new Metron(token, handler))
        {
            Check(provider.Configured && provider.Id == "metron", "Configured provider identity was not recognized.");
            var volumes = await provider.VolumesAsync(" Example & Friends ", 100);
            Check(volumes.Limit == 100 && volumes.Offset == 100 && volumes.Total == 101 && volumes.Results.Single() ==
                new MetadataVolume(12, "Example & Friends (1999)", "1999", "Publisher", seriesUrl) { Ordinal = 2 }, "Series list mapping failed.");
            await provider.VolumesAsync("Example & Friends", 100);
            Check(handler.Calls == 1, "Repeated search missed the cache.");
            var issues = await provider.IssuesAsync(12, "1/2", 100);
            Check(issues.Limit == 100 && issues.Offset == 100 && issues.Total == 101 && issues.Results.Single().Number == "1/2"
                && issues.Results[0].StoreDate == "2001-01-17" && issues.Results[0].CoverUrl == "https://static.metron.cloud/media/issue/cover.jpg", "Issue list mapping failed.");
            var metadata = await provider.MetadataAsync(34, 12);
            var fields = metadata.Fields;
            Check(metadata.Url == issueUrl && fields["Web"] == issueUrl && fields["Series"] == "Example & Friends" && fields["Publisher"] == "Publisher"
                && fields["Title"] == "A title; Another story" && fields["Number"] == "1/2" && fields["Year"] == "2001" && fields["Month"] == "2" && fields["Day"] == "3"
                && fields["Writer"] == "Creator" && fields["Penciller"] == "Creator" && fields["CoverArtist"] == "Creator"
                && fields["Colorist"] == "Color Person" && fields["Editor"] == "Color Person" && fields["Characters"] == "Hero" && fields["StoryArc"] == "Story"
                && fields["Teams"] == "Team" && fields["Imprint"] == "Imprint" && fields["Genre"] == "Adventure" && fields["LanguageISO"] == "en"
                && fields["Format"] == "Single Issue" && fields["PageCount"] == "32" && fields["AgeRating"] == "Teen" && fields["AlternateNumber"] == "7",
                "Real Metron metadata mapping or credit deduplication failed.");
            Check(fields["Summary"] == "Hello & welcome Second paragraph 😀\n\nSources: Wikipedia (https://en.wikipedia.org/wiki/Example)"
                && !fields.ContainsKey("Volume") && fields.Values.All(value => !string.IsNullOrWhiteSpace(value))
                && fields.Keys.All(name => LibraryStore.MetadataFields.Any(field => field.Name == name)) && !JsonSerializer.Serialize(metadata).Contains(token),
                "Metadata includes unsafe markup, secrets, unsupported fields, or lost attribution.");
            await provider.MetadataAsync(34, 12);
            var processing = await provider.MetadataAsync(34, 12, intake: true);
            Check(processing.Fields["Month"] == "1" && processing.Fields["Day"] == "17" && processing.Fields["Volume"] == "1999" && handler.Calls == 3,
                "Intake lost release date, series start year or caching.");
            Check(handler.Times.Zip(handler.Times.Skip(1)).All(pair => pair.Second - pair.First >= TimeSpan.FromMilliseconds(2900)), "20/minute request pacing was bypassed.");
            await Error(() => provider.MetadataAsync(34, 99), 409);
            foreach (var query in new[] { "", " ", new string('a', 201), "a\nb" }) await Error(() => provider.VolumesAsync(query, 0), 400);
            foreach (var offset in new[] { -1, 1, 10, 10001 }) await Error(() => provider.VolumesAsync("Example", offset), 400);
            foreach (var number in new[] { "1,series_id=9", "1|2", "1%2c", "1&number=x", "1=2", "1?2", "1#2", "1\n2", new string('1', 41) })
                await Error(() => provider.IssuesAsync(12, number, 0), 400);
            await Error(() => provider.IssuesAsync(0, null, 0), 400);
            await Error(() => provider.IssuesAsync(12, null, 20), 400);
            await Error(() => provider.MetadataAsync(-1, 12), 400);
            Check(handler.Calls == 3, "Invalid input or mismatched selection reached Metron.");
        }
        var paged = new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/issue/" => Json(new { count = 2, results = new[] {
                new { id = 34, series = new { id = 12, name = "Example", volume = 1, year_began = 2025 }, number = "15", cover_date = "2026-11-04" },
                new { id = 35, series = new { id = 13, name = "Example", volume = 2, year_began = 2026 }, number = "15", cover_date = "2026-11-04" } } }),
            "/api/series/" => Json(new { count = 101, results = new[] {
                new { id = request.RequestUri.Query.Contains("page=2") ? 13 : 12, series = "Example",
                    year_began = 2025, publisher = new { name = "Publisher" } } } }),
            _ => throw new Exception("Matching fetched per-series details instead of the next series page.")
        });
        using (var provider = new Metron(token, paged))
        {
            var matches = await provider.MatchIntakeAsync(new("Example", "015", 2026, null, "Publisher"));
            Check(matches.Candidates.Select(candidate => candidate.IssueId).Order().SequenceEqual(new[] { 34, 35 })
                && matches.Candidates.All(candidate => candidate.Publisher == "Publisher") && paged.Calls == 3,
                "Paged series enrichment dropped candidates, publishers, or fanned out into detail requests.");
        }
        foreach (var image in new[] { "http://static.metron.cloud/media/a.jpg", "https://evil.example/media/a.jpg", "https://static.metron.cloud.evil.example/media/a.jpg",
            "https://user@static.metron.cloud/media/a.jpg", "https://static.metron.cloud:444/media/a.jpg", "https://metron.cloud/api/issue/34/",
            "https://static.metron.cloud/media/a.jpg?token=private", "https://static.metron.cloud/media/a.jpg#fragment", "javascript:alert(1)" })
        {
            using var provider = new Metron(token, new Handler(_ => Json(new { count = 1, next = "https://evil.example/", previous = (string?)null,
                results = new[] { new { id = 34, series = new { id = 12 }, image, resource_url = image } } })));
            var result = await provider.IssuesAsync(12, null, 0);
            Check(result.Results.Single().CoverUrl == "" && result.Results[0].Url == issueUrl, "Unsafe image or upstream URL escaped.");
        }
        using (var provider = new Metron(token, new Handler(_ => Json(new { count = 1, results = new[] { new { id = 34, series = new { id = 99 } } } }))))
            await Error(() => provider.IssuesAsync(12, null, 0), 502);
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.NotFound, HttpStatusCode.Found, HttpStatusCode.ServiceUnavailable })
        {
            using var provider = new Metron(token, new Handler(_ => new HttpResponseMessage(status) { Content = new StringContent(token) }));
            await Error(() => provider.VolumesAsync("Example", 0), status == HttpStatusCode.NotFound ? 404 : 502);
        }
        foreach (var dated in new[] { false, true })
        {
            var limited = new Handler(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent(token) };
                response.Headers.RetryAfter = dated ? new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddMinutes(2))
                    : new RetryConditionHeaderValue(TimeSpan.FromMinutes(2));
                return response;
            });
            using var provider = new Metron(token, limited);
            await Error(() => provider.VolumesAsync("Example", 0), 429);
            await Error(() => provider.IssuesAsync(12, null, 0), 429);
            Check(limited.Calls == 1, "Rate limit was retried or cooldown was not global.");
        }
        foreach (var body in new[] { token, "{}", "[]", "{\"count\":1,\"results\":null}", "{\"count\":-1,\"results\":[]}", new string('x', 1024 * 1024 + 1),
            JsonSerializer.Serialize(new { count = 101, results = Enumerable.Range(1, 101).Select(id => new { id }) }) })
        {
            using var provider = new Metron(token, new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }));
            await Error(() => provider.VolumesAsync("Example", 0), 502);
        }
        using (var provider = new Metron(token, new Handler(_ => throw new HttpRequestException(token))))
            await Error(() => provider.VolumesAsync("Example", 0), 502);
        using (var provider = new Metron(token, new Handler(_ => throw new TaskCanceledException(token))))
            await Error(() => provider.VolumesAsync("Example", 0), 504);
        using (var provider = new Metron(token, new Handler(_ => throw new Exception("Cancelled request reached the provider."))))
        {
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try { await provider.VolumesAsync("Example", 0, cancelled.Token); throw new Exception("Cancellation was ignored."); }
            catch (OperationCanceledException exception) { Check(exception.InnerException is null && !exception.ToString().Contains(token), "Cancellation leaked credentials."); }
        }
        using (var provider = new Metron(token, new Handler(_ => Json(new { id = 34, series = new { id = 12 }, title = (string?)null,
            name = Array.Empty<string>(), desc = "<p> </p>", cover_date = "bad", store_date = "bad" }))))
        {
            var metadata = await provider.MetadataAsync(34, 12, intake: true);
            Check(metadata.Fields.Count == 1 && metadata.Fields["Web"] == issueUrl, "Absent metadata became empty replacements or invented dates.");
        }
        using (var provider = new Metron(token, new Handler(_ => Json(new { id = 34, series = new { id = 12 }, title = "Collected title",
            name = new[] { "Story" }, cover_date = "2025-02-03", store_date = "bad" }))))
        {
            var metadata = await provider.MetadataAsync(34, 12, intake: true);
            Check(metadata.Fields["Title"] == "Collected title" && metadata.Fields["Year"] == "2025" && metadata.Fields["Month"] == "2", "Title or cover-date fallback failed.");
        }
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) };
    private static async Task Error(Func<Task> action, int status)
    {
        try { await action(); }
        catch (MetadataProviderException exception)
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
