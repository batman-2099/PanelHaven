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
                        id = 34, volume = new { id = 12 }, name = "<b>A title</b>", issue_number = "1/2", cover_date = "2001-02-03", store_date = "2001-01-17",
                        description = "<p>Hello &amp; welcome</p><script>doNotImport()</script><p>Second\u0001 paragraph 😀</p>",
                        site_detail_url = issueUrl + "?tracking=ignored#fragment", person_credits = new[] {
                            new { name = "Creator", role = "writer, penciler, cover" }, new { name = "Creator", role = "writer" },
                            new { name = "Color Person", role = "colorist, editor" }, new { name = "Ignored", role = "other" } },
                        character_credits = new[] { new { name = "Hero" }, new { name = "Hero" } },
                        story_arc_credits = new[] { new { name = "Story" } }
                    } });
                case "/api/volume/4050-12/":
                    return Json(new { status_code = 1, results = new { id = 12, name = "Example &amp; Friends", start_year = "1999", publisher = new { name = "Publisher" } } });
                default: throw new Exception("Unexpected provider resource.");
            }
        });
        using (var provider = new ComicVine(key, handler))
        {
            Check(provider.Configured && provider.Id == "comicvine", "Configured provider identity was not recognized.");
            var volumes = await provider.VolumesAsync(" Example & Friends ", 10);
            Check(volumes.Limit == 10 && volumes.Offset == 10 && volumes.Total == 11 && volumes.Results.Single() ==
                new MetadataVolume(12, "Example & Friends", "1999", "Publisher", volumeUrl), "Volume results changed or lost fields.");
            await provider.VolumesAsync("Example & Friends", 10);
            Check(handler.Calls == 1, "Repeated search missed the cache.");
            var issues = await provider.IssuesAsync(12, "1/2", 20);
            Check(issues.Limit == 20 && issues.Offset == 20 && issues.Total == 21 && issues.Results.Single().Name == ""
                && issues.Results[0].Number == "1/2", "Issue results lost optional fields or paging.");
            var metadata = await provider.MetadataAsync(34, 12);
            var fields = metadata.Fields;
            Check(metadata.Url == issueUrl && fields["Web"] == issueUrl && fields["Series"] == "Example & Friends" && fields["Publisher"] == "Publisher"
                && fields["Title"] == "A title" && fields["Number"] == "1/2" && fields["Year"] == "2001" && fields["Month"] == "2" && fields["Day"] == "3"
                && fields["Writer"] == "Creator" && fields["Penciller"] == "Creator" && fields["CoverArtist"] == "Creator"
                && fields["Colorist"] == "Color Person" && fields["Editor"] == "Color Person" && fields["Characters"] == "Hero" && fields["StoryArc"] == "Story",
                "Metadata mapping or credit deduplication failed.");
            Check(fields["Summary"] == "Hello & welcome Second paragraph 😀" && !fields.ContainsKey("Genre") && !fields.ContainsKey("Volume")
                && fields.Values.All(value => !string.IsNullOrWhiteSpace(value)) && fields.Keys.All(name => LibraryStore.MetadataFields.Any(field => field.Name == name)),
                "Metadata includes HTML, invalid XML, invented fields, or empty replacements.");
            await provider.MetadataAsync(34, 12);
            Check(handler.Calls == 3, "Metadata did not reuse searched volume details and the cached issue response.");
            var processing = await provider.MetadataAsync(34, 12, intake: true);
            Check(processing.Fields["Year"] == "2001" && processing.Fields["Month"] == "1"
                && processing.Fields["Day"] == "17" && processing.Fields["Volume"] == "1999" && handler.Calls == 3,
                "Intake processing lost release-date precedence, volume start year, or shared provider cache.");
            Check(handler.Times.Zip(handler.Times.Skip(1)).All(pair => pair.Second - pair.First >= TimeSpan.FromMilliseconds(900)), "Global velocity throttle was bypassed.");
            await Error(() => provider.MetadataAsync(34, 99), 409);
            Check(handler.Calls == 3, "Volume mismatch fetched an unrelated volume.");

            foreach (var query in new[] { "", " ", new string('a', 201), "a\nb" }) await Error(() => provider.VolumesAsync(query, 0), 400);
            foreach (var offset in new[] { -1, 1, 10010 }) await Error(() => provider.VolumesAsync("Example", offset), 400);
            foreach (var number in new[] { "1,volume:9", "1|2", "1%2cvolume", "1&filter=x", "1=2", "1?2", "1#2", "1\n2", new string('1', 41) })
                await Error(() => provider.IssuesAsync(12, number, 0), 400);
            await Error(() => provider.IssuesAsync(0, null, 0), 400);
            await Error(() => provider.IssuesAsync(12, null, 10), 400);
            await Error(() => provider.MetadataAsync(-1, 12), 400);
            Check(handler.Calls == 3, "Invalid input reached Comic Vine.");
        }

        foreach (var searchFirst in new[] { false, true })
        {
            var fallback = new Handler(request => request.RequestUri!.AbsolutePath switch
            {
                "/api/search/" => Json(new { status_code = 1, number_of_total_results = 1,
                    results = new[] { new { id = 12, name = "Incomplete", site_detail_url = volumeUrl } } }),
                "/api/issue/4000-34/" => Json(new { status_code = 1,
                    results = new { id = 34, volume = new { id = 12 }, site_detail_url = issueUrl } }),
                "/api/volume/4050-12/" => Json(new { status_code = 1,
                    results = new { id = 12, name = "Example", start_year = "1999", publisher = new { name = "Publisher" } } }),
                _ => throw new Exception("Unexpected provider resource.")
            });
            using var provider = new ComicVine(key, fallback);
            if (searchFirst) await provider.VolumesAsync("Example", 0);
            var metadata = await provider.MetadataAsync(34, 12, intake: true);
            Check(metadata.Fields["Series"] == "Example" && metadata.Fields["Publisher"] == "Publisher"
                && metadata.Fields["Volume"] == "1999" && fallback.Calls == (searchFirst ? 3 : 2),
                "Missing or incomplete search data must fetch full volume details.");
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
            var failure = new Handler(_ => Json(new { status_code = status, error = key }));
            using var provider = new ComicVine(key, failure);
            await Error(() => provider.VolumesAsync("Example", 0), status == 101 ? 404 : status == 107 ? 429 : 502);
            if (status == 107)
            {
                await Error(() => provider.IssuesAsync(12, null, 0), 429);
                Check(failure.Calls == 1, "Provider status 107 did not block further requests during cooldown.");
            }
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
        using (var provider = new ComicVine(key, new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/search/" => Json(new { status_code = 1, number_of_total_results = 1, results = new[] {
                new { id = 12, name = "Example Vol. 2", start_year = "2020", publisher = new { name = "Publisher" }, site_detail_url = volumeUrl } } }),
            "/api/volume/4050-12/" => Json(new { status_code = 1, results = new {
                id = 12, name = "Example Vol. 2", start_year = "2020", publisher = new { name = "Publisher" } } }),
            "/api/issues/" => Json(new { status_code = 1, number_of_total_results = 1, results = new[] {
                new { id = 34, name = "Title", issue_number = "1", cover_date = "2025-01-01", site_detail_url = issueUrl } } }),
            _ => throw new Exception("Unexpected ordinal mapping request.")
        })))
        {
            var volumes = await provider.VolumesAsync("Example", 0);
            Check(volumes.Results.Single().Ordinal == 2, "Explicit Comic Vine name ordinal was not mapped.");
            var result = await provider.MatchIntakeAsync(new IntakeHints(volumeUrl, "1", 2025, 2, "Publisher"));
            Check(result.Candidates.Single().Score == 100 && result.Candidates[0].Reason.Contains("volume matches")
                && result.Candidates[0].Provider == "comicvine", "Exact URL lost explicit ordinal ranking or provider identity.");
        }
        await ComicVineIntakeMatchingChecks.RunAsync();
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

internal static class ComicVineIntakeMatchingChecks
{
    public static async Task RunAsync()
    {
        var hints = new IntakeHints("Example", "001", 2025, null, "Marvel");
        using (var missing = new ComicVine(null)) await Error(() => missing.MatchIntakeAsync(hints), 503);
        var handler = new Handler(request =>
        {
            var query = Uri.UnescapeDataString(request.RequestUri!.Query);
            Check(!query.Contains("/private") && request.RequestUri.Host == "comicvine.gamespot.com", "Private path or foreign provider origin used.");
            if (request.RequestUri.AbsolutePath == "/api/search/")
            {
                Check(query.Contains("limit=10") && query.Contains("offset=0"), "Initial volume search exceeded the first-ten bound.");
                return Json(new { status_code = 1, number_of_total_results = 15, results = new[] {
                    Volume(4, "Unrelated", "Other"), Volume(3, "Example", "Marvel Comics"), Volume(2, "Example", "Marvel"), Volume(1, "Example", "Marvel") } });
            }
            Check(query.Contains("limit=20") && query.Contains("offset=0"), "Matching exceeded one twenty-issue page per volume.");
            Check(query.Contains("issue_number:1") && query.Contains("store_date"), "Issue padding or release-date request was lost.");
            var id = query.Contains("volume:1,") ? 1 : query.Contains("volume:2,") ? 2 : query.Contains("volume:3,") ? 3 : throw new Exception("Unbounded/unranked volume request.");
            return Json(new { status_code = 1, number_of_total_results = 1, results = new[] {
                new { id = 100 + id, name = "Issue title", issue_number = "1", cover_date = "2026-01-01",
                    image = new { small_url = id == 1 ? "https://comicvine.gamespot.com/a/uploads/scale_small/cover.jpg"
                        : id == 2 ? "https://untrusted.example/cover.jpg" : "https://comicvine.gamespot.com/a/uploads/cover.jpg?api_key=private" },
                    store_date = id == 1 ? "2024-12-01" : "2025-12-01", site_detail_url = $"https://comicvine.gamespot.com/issue/4000-{100 + id}/" } } });
        });
        using (var provider = new ComicVine("check-secret-never-return", handler))
        {
            var result = await provider.MatchIntakeAsync(hints);
            Check(result.Candidates.Select(candidate => candidate.IssueId).SequenceEqual([102, 103, 101]), "Ranking ignored release year/publisher or deterministic ties.");
            Check(result.Candidates[0].Score == 95 && result.Candidates[0].Reason.Contains("release year 2025 matches")
                && result.Candidates.All(candidate => candidate.Score is >= 0 and <= 100 && candidate.Provider == "comicvine"), "Score/reason is incorrect.");
            Check(result.Candidates.Single(candidate => candidate.IssueId == 101).CoverUrl == "https://comicvine.gamespot.com/a/uploads/scale_small/cover.jpg"
                && result.Candidates.Where(candidate => candidate.IssueId != 101).All(candidate => candidate.CoverUrl == ""),
                "Trusted cover was lost or an untrusted/credential-bearing image URL escaped.");
            Check(result.Warnings.Any(warning => warning.Contains("Ambiguous")) && result.Warnings.Any(warning => warning.Contains("bounded")),
                "Ambiguity or bounded-search disclosure was omitted.");
            await provider.MatchIntakeAsync(hints);
            Check(handler.Calls == 4, "Matching bypassed the existing cache or fetched excessive pages.");
            await Error(() => provider.MatchIntakeAsync(hints with { Number = "1?2" }), 400);
            Check(handler.Calls == 4, "Invalid matching hints reached the provider.");
        }
        var recent = new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/search/")
                return Json(new { status_code = 1, number_of_total_results = 5,
                    results = new[] { "", "1990", "2020", "2025", "2026" }.Select((year, index) => new {
                        id = index + 1, name = index == 4 ? "Unrelated" : "Example", start_year = year,
                        publisher = new { name = "Marvel" },
                        site_detail_url = $"https://comicvine.gamespot.com/volume/4050-{index + 1}/" }) });
            var query = Uri.UnescapeDataString(request.RequestUri.Query);
            var id = Enumerable.Range(1, 5).Single(id => query.Contains($"volume:{id},"));
            return Json(new { status_code = 1, number_of_total_results = 1, results = new[] {
                new { id = 100 + id, name = "Issue", issue_number = "1", cover_date = "2025-01-01",
                    site_detail_url = $"https://comicvine.gamespot.com/issue/4000-{100 + id}/" } } });
        });
        using (var provider = new ComicVine("key", recent))
        {
            var noYear = hints with { Year = null };
            var result = await provider.MatchIntakeAsync(noYear);
            Check(result.Candidates.Select(candidate => candidate.VolumeId).SequenceEqual([4, 3, 2]),
                "Newer equally matching runs must enter the shortlist and lead results, ahead of unknown years but not better series matches.");
            result = await provider.MatchIntakeAsync(noYear with { Volume = 1990 });
            Check(result.Candidates[0].VolumeId == 2, "Recency overrode an explicit volume match.");
            result = await provider.MatchIntakeAsync(noYear with { Year = 1995 });
            Check(result.Candidates.Any(candidate => candidate.VolumeId == 2),
                "Newer runs displaced the run matching the historical year hint.");
        }
        var exact = new Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/volume/4050-166619/")
                return Json(new { status_code = 1, results = new { id = 166619, name = "Batman",
                    start_year = "2025", publisher = new { name = "DC Comics" } } });
            if (path == "/api/issues/")
            {
                Check(Uri.UnescapeDataString(request.RequestUri.Query).Contains("filter=volume:166619,issue_number:1"),
                    "Exact series matching searched a different run or lost issue normalization.");
                return Json(new { status_code = 1, number_of_total_results = 1, results = new[] {
                    new { id = 34, name = "Batman #1", issue_number = "1", cover_date = "2025-09-01",
                        site_detail_url = "https://comicvine.gamespot.com/batman-1/4000-34/" } } });
            }
            if (path == "/api/issue/4000-34/")
                return Json(new { status_code = 1, results = new { id = 34, volume = new { id = 166619 },
                    name = "Batman #1", issue_number = "1", site_detail_url = "https://comicvine.gamespot.com/batman-1/4000-34/" } });
            throw new Exception("Exact series matching made a broad search or unrelated provider request.");
        });
        using (var provider = new ComicVine("key", exact))
        {
            var exactHints = hints with { Series = "https://comicvine.gamespot.com/batman/4050-166619/", Volume = 1940 };
            var result = await provider.MatchIntakeAsync(exactHints);
            Check(result.Candidates.Single().VolumeId == 166619 && result.Candidates[0].Series == "Batman"
                && result.Candidates[0].Number == "1" && exact.Calls == 2,
                "Explicit series URL failed to override an older run hint.");
            await provider.MatchIntakeAsync(exactHints);
            var metadata = await provider.MetadataAsync(34, 166619, intake: true);
            await provider.MetadataAsync(34, 166619, intake: true);
            Check(metadata.Fields["Series"] == "Batman" && metadata.Fields["Volume"] == "2025"
                && metadata.Fields["Publisher"] == "DC Comics" && exact.Calls == 3,
                "Acceptance lost exact-run metadata or repeated the volume lookup.");
            foreach (var url in new[] { "http://comicvine.gamespot.com/batman/4050-166619/",
                "https://evil.example/batman/4050-166619/", "https://comicvine.gamespot.com.evil.example/batman/4050-166619/",
                "https://user@comicvine.gamespot.com/batman/4050-166619/", "https://comicvine.gamespot.com:444/batman/4050-166619/",
                "https://comicvine.gamespot.com/batman/4000-34/", "https://comicvine.gamespot.com/batman/4050-0/",
                "https://comicvine.gamespot.com/batman/4050-99999999999999999/" })
                await Error(() => provider.MatchIntakeAsync(exactHints with { Series = url }), 400);
            Check(exact.Calls == 3, "Invalid series URLs reached the provider.");
        }
        var volume = new MetadataVolume(1, "Example", "2020", "Marvel Comics", "https://comicvine.gamespot.com/volume/4050-1/");
        var issue = new ProviderIssue(1, "Title", "1", "2025-01-01", "https://comicvine.gamespot.com/issue/4000-1/");
        Check(IntakeMatching.Rank(hints with { Volume = 2020 }, volume, issue).Score == 100
            && IntakeMatching.Rank(hints, volume, issue).Reason.Contains("cover year 2025 matches"), "Cover-date fallback or year-style volume ranking failed.");
        Check(IntakeMatching.Rank(hints with { Volume = 2 }, volume, issue).Reason.Contains("ordinal unavailable"), "Provider volume ID was mistaken for an ordinal.");
        Check(IntakeMatching.Rank(hints with { Volume = 2 }, volume with { Name = "Example Vol. 2", Ordinal = 2 }, issue).Score == 100,
            "An explicit matching volume suffix penalized the base series name or failed to rank.");
        foreach (var status in new[] { HttpStatusCode.TooManyRequests, HttpStatusCode.ServiceUnavailable })
        {
            var failing = new Handler(_ => new(status) { Content = new StringContent("check-secret-never-return") });
            using var provider = new ComicVine("check-secret-never-return", failing);
            await Error(() => provider.MatchIntakeAsync(hints), status == HttpStatusCode.TooManyRequests ? 429 : 502);
            if (status == HttpStatusCode.TooManyRequests)
            {
                await Error(() => provider.MatchIntakeAsync(hints), 429);
                Check(failing.Calls == 1, "Matching bypassed provider rate-limit cooldown.");
            }
        }
        using (var provider = new ComicVine("check-secret-never-return", new Handler(request => request.RequestUri!.AbsolutePath == "/api/search/"
            ? Json(new { status_code = 1, number_of_total_results = 1, results = new[] { Volume(1, "Example", "Marvel") } })
            : new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("check-secret-never-return") })))
            await Error(() => provider.MatchIntakeAsync(hints), 502);
        using (var provider = new ComicVine("key", new Handler(_ => Json(new { status_code = 1, number_of_total_results = 0, results = Array.Empty<object>() }))))
        {
            var result = await provider.MatchIntakeAsync(hints);
            Check(result.Candidates.Count == 0 && result.Warnings.Any(warning => warning.Contains("No candidates")), "Empty search lacks actionable explanation.");
        }
        using (var provider = new ComicVine("key", new Handler(_ => throw new Exception("Cancelled matching reached provider."))))
        {
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try { await provider.MatchIntakeAsync(hints, cancelled.Token); throw new Exception("Matching ignored cancellation."); }
            catch (OperationCanceledException) { }
        }
    }

    private static object Volume(int id, string name, string publisher) => new { id, name, start_year = "2020", publisher = new { name = publisher },
        site_detail_url = $"https://comicvine.gamespot.com/volume/4050-{id}/" };
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) };
    private static async Task Error(Func<Task> action, int status)
    {
        try { await action(); }
        catch (MetadataProviderException exception)
        {
            Check(exception.StatusCode == status && !exception.ToString().Contains("check-secret-never-return") && exception.InnerException is null,
                "Intake error status or secret sanitization failed.");
            return;
        }
        throw new Exception("Expected intake failure was not raised.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(respond(request));
        }
    }
}
