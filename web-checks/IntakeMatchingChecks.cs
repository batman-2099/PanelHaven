using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PanelHaven.Web;

internal static class IntakeMatchingChecks
{
    public static async Task RunAsync()
    {
        var empty = new Dictionary<string, string>();
        var parsed = IntakeMatching.Parse("Marvel Week 42 (2026)/Example v2 #001.50 (2025) (Digital).cbz", empty);
        Check(parsed == new IntakeHints("Example", "001.50", 2025, 2, "Marvel"), "Filename hints confused issue, year, volume or weekly publisher.");
        Check(IntakeMatching.Parse("2026-10-14/DC Comics/Example 001.cbz", empty)
            == new IntakeHints("Example", "001", 2026, null, "DC Comics"), "Dated folder hints were not extracted.");
        Check(IntakeMatching.Parse("2026.10.14/DC Week/Example 001.cbz", empty)
            == new IntakeHints("Example", "001", 2026, null, "DC"), "Publisher weekly-folder suffix leaked into the publisher hint.");
        Check(IntakeMatching.Parse("Marvel 2024-01-01/DC 2025-02-02/Example 1.cbz", empty).Publisher == "DC",
            "An outer weekly folder overrode the nearest folder.");
        foreach (var filename in new[] { "Example 2024 001.cbz", "Example 001 2024.cbz", "Example 001 (2024).cbz" })
            Check(IntakeMatching.Parse(filename, empty) == new IntakeHints("Example", "001", 2024, null, ""), "Bare/enclosed year parsing failed.");
        Check(IntakeMatching.Parse("Example v2020 001 (2026).cbr", empty).Volume == 2020,
            "A year-shaped volume was confused with the issue release year.");
        Check(IntakeMatching.Parse("2000 AD 001.cbz", empty).Series == "2000 AD", "Numeric series title was consumed as a year.");
        foreach (var number in new[] { "001AU", "-1", "½", "∞", "nn", "Special", "FCBD" })
            Check(IntakeMatching.Parse($"Example #{number}.cbz", empty).Number == number, "A special issue number was lost.");
        foreach (var number in new[] { "ALPHA", "OMEGA", "INFINITY" })
            Check(IntakeMatching.Parse($"Example {number}.cbz", empty) == new IntakeHints("Example", number, null, null, ""),
                "A standalone named special issue became part of the series.");
        Check(IntakeMatching.NormalizeNumber("INFINITY") == IntakeMatching.NormalizeNumber("∞"),
            "Named and symbolic infinity issues did not normalize equally.");
        Check(IntakeMatching.NormalizeNumber("001.50") == "1.5" && IntakeMatching.NormalizeNumber("001AU") == "1AU"
            && IntakeMatching.NormalizeNumber("1/2") == IntakeMatching.NormalizeNumber("½"), "Issue normalization changed numeric meaning.");
        var fallback = new Dictionary<string, string> { ["Series"] = "Catalog", ["Number"] = "1/2", ["Year"] = "2001", ["Volume"] = "3", ["Publisher"] = "Known" };
        Check(IntakeMatching.Parse("download.cbz", fallback) == new IntakeHints("Catalog", "1/2", 2001, 3, "Known"), "Catalog fallback was lost.");
        var hints = new IntakeHints("Example", "001", 2025, null, "Marvel");
        foreach (var invalid in new IntakeHints?[] { null, hints with { Series = " " }, hints with { Publisher = new string('x', 201) },
            hints with { Series = "bad\nname" }, hints with { Number = "1,volume:9" }, hints with { Number = "1|2" },
            hints with { Number = "1%2c" }, hints with { Number = "1&filter=x" }, hints with { Number = "#1" },
            hints with { Number = new string('1', 41) }, hints with { Year = 0 }, hints with { Year = 10000 },
            hints with { Volume = -1 }, hints with { Number = null! } })
            await Error(() => Task.FromResult(IntakeMatching.Validate(invalid)), 400);
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        foreach (var json in new[] { "{}", "{\"series\":\"Example\",\"number\":\"1\",\"year\":null,\"volume\":null,\"publisher\":\"\",\"path\":\"/private\"}",
            "{\"series\":\"Example\",\"number\":\"1\",\"year\":\"2025\",\"volume\":null,\"publisher\":\"\"}" })
        {
            try { JsonSerializer.Deserialize<IntakeHints>(json, jsonOptions); throw new Exception("Malformed hints JSON was accepted."); }
            catch (JsonException) { }
        }
        using (var missing = new ComicVine(null)) await Error(() => missing.MatchIntakeAsync(hints), 503);
        var handler = new Handler(request =>
        {
            var query = Uri.UnescapeDataString(request.RequestUri!.Query);
            Check(!query.Contains("/private") && request.RequestUri.Host == "comicvine.gamespot.com", "Private path or foreign provider origin used.");
            if (request.RequestUri.AbsolutePath == "/api/search/")
                return Json(new { status_code = 1, number_of_total_results = 15, results = new[] {
                    Volume(4, "Unrelated", "Other"), Volume(3, "Example", "Marvel Comics"), Volume(2, "Example", "Marvel"), Volume(1, "Example", "Marvel") } });
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
                && result.Candidates.All(candidate => candidate.Score is >= 0 and <= 100), "Score/reason is incorrect.");
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
        var volume = new ComicVineVolume(1, "Example", "2020", "Marvel Comics", "https://comicvine.gamespot.com/volume/4050-1/");
        var issue = new ComicVineIssue(1, "Title", "1", "2025-01-01", "https://comicvine.gamespot.com/issue/4000-1/");
        Check(IntakeMatching.Rank(hints with { Volume = 2020 }, volume, issue).Score == 100
            && IntakeMatching.Rank(hints, volume, issue).Reason.Contains("cover year 2025 matches"), "Cover-date fallback or year-style volume ranking failed.");
        Check(IntakeMatching.Rank(hints with { Volume = 2 }, volume, issue).Reason.Contains("ordinal unavailable"), "Provider volume ID was mistaken for an ordinal.");
        Check(IntakeMatching.Rank(hints with { Volume = 2 }, volume with { Name = "Example Vol. 2" }, issue).Score == 100,
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
        await StoreChecks();
    }

    private static async Task StoreChecks()
    {
        var directory = Path.Combine(Path.GetTempPath(), "panelhaven-intake-" + Guid.NewGuid().ToString("N"));
        var regular = Path.Combine(directory, "Library");
        var unsorted = Path.Combine(directory, "Unsorted");
        var nearby = unsorted + "-nearby";
        var database = Path.Combine(directory, "state", "library.sqlite");
        var folder = Path.Combine(unsorted, "Marvel Week 42 (2026)");
        var source = Path.Combine(folder, "Example v2 001.cbz");
        try
        {
            foreach (var path in new[] { regular, nearby, folder }) Directory.CreateDirectory(path);
            using (var comic = ZipFile.Open(source, ZipArchiveMode.Create))
            {
                using var page = comic.CreateEntry("page.png").Open();
                page.Write(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a3ioAAAAASUVORK5CYII="));
            }
            var ordinary = Path.Combine(regular, "ordinary.cbz");
            File.Copy(source, ordinary);
            File.Copy(source, Path.Combine(nearby, "nearby.cbz"));
            var store = new LibraryStore(database, [regular, nearby], unsortedPath: unsorted);
            Check((await store.ScanAsync(default)).Added == 3, "Intake fixture scan failed.");
            var book = store.ListBooks("check", null, location: "unsorted").Items.Single();
            var bytes = File.ReadAllBytes(source);
            var metadata = JsonSerializer.Serialize(store.GetMetadata(book.Id));
            var intake = store.GetIntake(book.Id);
            Check(intake.Hints == new IntakeHints("Example", "001", 2026, 2, "Marvel")
                && intake.Source == "Unsorted/Marvel Week 42 (2026)/Example v2 001.cbz"
                && !JsonSerializer.Serialize(intake).Contains(directory), "Intake leaked an absolute path or lost source hints.");
            Check(bytes.SequenceEqual(File.ReadAllBytes(source)) && metadata == JsonSerializer.Serialize(store.GetMetadata(book.Id)),
                "Hint extraction mutated source or catalog metadata.");
            foreach (var libraryBook in store.ListBooks("check", null).Items)
            {
                var match = store.GetIntake(libraryBook.Id);
                Check(match.Source.StartsWith("Library ", StringComparison.Ordinal) && !match.Source.Contains(directory)
                    && match.Hints.Series == Path.GetFileNameWithoutExtension(match.Source),
                    "Catalog matching must support ordinary libraries without exposing absolute paths.");
            }
            using var db = new SqliteConnection($"Data Source={database}");
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "UPDATE books SET availability='unavailable' WHERE id=$id";
            command.Parameters.AddWithValue("$id", book.Id);
            command.ExecuteNonQuery();
            await Error(() => Task.FromResult(store.GetIntake(book.Id)), 409);
            command.CommandText = "UPDATE books SET availability='available' WHERE id=$id";
            command.ExecuteNonQuery();
            File.Delete(source);
            try { store.GetIntake(book.Id); throw new Exception("Missing intake source was accepted."); }
            catch (FileNotFoundException) { }
            if (!OperatingSystem.IsWindows())
            {
                File.CreateSymbolicLink(source, ordinary);
                try { store.GetIntake(book.Id); throw new Exception("Symlink intake source was accepted."); }
                catch (UnauthorizedAccessException) { }
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    private static object Volume(int id, string name, string publisher) => new { id, name, start_year = "2020", publisher = new { name = publisher },
        site_detail_url = $"https://comicvine.gamespot.com/volume/4050-{id}/" };
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) };
    private static async Task Error(Func<Task> action, int status)
    {
        try { await action(); }
        catch (ComicVineException exception)
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
