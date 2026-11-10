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
        using (var missing = new Metron(null)) await Error(() => missing.MatchIntakeAsync(hints), 503);
        var handler = new Handler(request =>
        {
            var query = Uri.UnescapeDataString(request.RequestUri!.Query);
            Check(!query.Contains("/private") && request.RequestUri.Host == "metron.cloud", "Private path or foreign origin used.");
            if (request.RequestUri.AbsolutePath == "/api/series/")
                return Json(new { count = 200, next = "https://metron.cloud/api/series/?page=2", previous = (string?)null,
                    results = new[] { Volume(1, "Example", "Marvel"), Volume(2, "Example", "Marvel"), Volume(3, "Example", "Marvel Comics") } });
            Check(request.RequestUri.AbsolutePath == "/api/issue/" && query.Contains("series_name=Example")
                && query.Contains("number=1") && query.Contains("page=1") && !query.Contains("cover_year") && !query.Contains("series_year_began"),
                "Matching must filter issues directly without confusing issue year with series year.");
            return Json(new { count = 101, next = "https://evil.example/?page=2", previous = (string?)null,
                results = Enumerable.Range(1, 3).Select(id => new {
                    id = 100 + id, series = new { id, name = "Example", volume = 2, year_began = 2020 }, number = "1",
                    issue = "Example (2020) #1", cover_date = "2026-01-01", store_date = id == 1 ? "2024-12-01" : "2025-12-01",
                    image = id == 1 ? "https://static.metron.cloud/media/issue/cover.jpg"
                        : id == 2 ? "https://untrusted.example/cover.jpg" : "https://metron.cloud/media/cover.jpg?token=private" }) });
        });
        using (var provider = new Metron("check-secret-never-return", handler))
        {
            var result = await provider.MatchIntakeAsync(hints);
            Check(result.Candidates.Select(candidate => candidate.IssueId).SequenceEqual([102, 103, 101]), "Ranking ignored release year/publisher or deterministic ties.");
            Check(result.Candidates[0].Score == 95 && result.Candidates[0].Reason.Contains("release year 2025 matches")
                && result.Candidates.All(candidate => candidate.Score is >= 0 and <= 100 && candidate.Provider == "metron"), "Score/reason or provider identity is incorrect.");
            Check(result.Candidates.Single(candidate => candidate.IssueId == 101).CoverUrl == "https://static.metron.cloud/media/issue/cover.jpg"
                && result.Candidates.Where(candidate => candidate.IssueId != 101).All(candidate => candidate.CoverUrl == ""), "Cover allowlist failed.");
            Check(result.Warnings.Any(warning => warning.Contains("Ambiguous")) && result.Warnings.Any(warning => warning.Contains("first 100")), "Search-limit disclosure missing.");
            await provider.MatchIntakeAsync(hints);
            Check(handler.Calls == 2, "Matching cache or shared series enrichment failed.");
            await Error(() => provider.MatchIntakeAsync(hints with { Number = "1?2" }), 400);
            Check(handler.Calls == 2, "Invalid hints reached the provider.");
        }
        var recent = new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/series/")
                return Json(new { count = 11, results = Enumerable.Range(1, 11).Select(id => Volume(id, "Example", "Marvel")) });
            if (request.RequestUri.AbsolutePath == "/api/series/12/")
                return Json(new { id = 12, name = "Example", year_began = 2025, volume = 3, publisher = new { id = 1, name = "Marvel" } });
            Check(request.RequestUri.AbsolutePath == "/api/issue/", "Unexpected recent-series request.");
            return Json(new { count = 4, results = new[] { (Id: 1, Year: 1990), (Id: 2, Year: 2020), (Id: 11, Year: 2024), (Id: 12, Year: 2025) }
                .Select(item => new { id = 100 + item.Id, series = new { id = item.Id, name = "Example", volume = item.Id == 12 ? 3 : 1, year_began = item.Year },
                    number = "1", issue = "Example #1", cover_date = "2025-01-01", store_date = (string?)null }) });
        });
        using (var provider = new Metron("token", recent))
        {
            var noYear = hints with { Year = null };
            var result = await provider.MatchIntakeAsync(noYear);
            Check(result.Candidates.Select(candidate => candidate.VolumeId).SequenceEqual([12, 11, 2, 1]),
                "Matching retained a first-ten-series ceiling, dropped an unlisted run, or lost recency tie-breaking.");
            result = await provider.MatchIntakeAsync(noYear with { Volume = 1990 });
            Check(result.Candidates[0].VolumeId == 1, "Recency overrode an explicit series-year match.");
            result = await provider.MatchIntakeAsync(noYear with { Volume = 3 });
            Check(result.Candidates[0].VolumeId == 12 && result.Candidates[0].Reason.Contains("volume matches"), "Native Metron ordinal was not ranked.");
            result = await provider.MatchIntakeAsync(noYear with { Year = 1995 });
            Check(result.Candidates.Any(candidate => candidate.VolumeId == 1), "Year hint filtered out a run.");
        }
        var exact = new Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/series/166619/")
                return Json(new { id = 166619, name = "Batman", year_began = 2025, volume = 4, publisher = new { id = 2, name = "DC Comics" } });
            if (path == "/api/issue/")
            {
                Check(Uri.UnescapeDataString(request.RequestUri.Query).Contains("series_id=166619&number=1&page=1"), "Exact series filter failed.");
                return Json(new { count = 1, results = new[] {
                    new { id = 34, series = new { id = 166619, name = "Batman", volume = 4, year_began = 2025 },
                        number = "1", issue = "Batman (2025) #1", cover_date = "2025-09-01", store_date = "2025-08-20" } } });
            }
            if (path == "/api/issue/34/")
                return Json(new { id = 34, series = new { id = 166619, name = "Batman", volume = 4, year_began = 2025 },
                    publisher = new { id = 2, name = "DC Comics" }, title = "", name = new[] { "Batman #1" }, number = "1",
                    cover_date = "2025-09-01", store_date = "2025-08-20" });
            throw new Exception("Exact series matching made a broad or unrelated request.");
        });
        using (var provider = new Metron("token", exact))
        {
            var exactHints = hints with { Series = "https://metron.cloud/series/166619/", Volume = 1940 };
            var result = await provider.MatchIntakeAsync(exactHints);
            Check(result.Candidates.Single().VolumeId == 166619 && result.Candidates[0].Series == "Batman"
                && result.Candidates[0].Number == "1" && exact.Calls == 2, "Exact series URL failed to override an old hint.");
            await provider.MatchIntakeAsync(exactHints);
            var metadata = await provider.MetadataAsync(34, 166619, intake: true);
            await provider.MetadataAsync(34, 166619, intake: true);
            Check(metadata.Fields["Series"] == "Batman" && metadata.Fields["Volume"] == "2025"
                && metadata.Fields["Publisher"] == "DC Comics" && metadata.Fields["Month"] == "8" && exact.Calls == 3,
                "Acceptance lost exact-run metadata or cached details.");
            await Error(() => provider.MetadataAsync(34, 1940, intake: true), 409);
            foreach (var url in new[] { "http://metron.cloud/series/166619/", "https://evil.example/series/166619/",
                "https://metron.cloud.evil.example/series/166619/", "https://user@metron.cloud/series/166619/",
                "https://metron.cloud:444/series/166619/", "https://metron.cloud/issue/34/", "https://metron.cloud/series/0/",
                "https://metron.cloud/series/99999999999999999/", "https://metron.cloud/series/166619/?token=private",
                "https://metron.cloud/series/166619/#fragment", "ftp://metron.cloud/series/166619/" })
                await Error(() => provider.MatchIntakeAsync(exactHints with { Series = url }), 400);
            Check(exact.Calls == 3, "Invalid exact-series URLs reached Metron.");
        }
        var volume = new MetadataVolume(1, "Example", "2020", "Marvel Comics", "https://metron.cloud/series/1/");
        var issue = new ProviderIssue(1, "Title", "1", "2025-01-01", "https://metron.cloud/issue/1/");
        Check(IntakeMatching.Rank(hints with { Volume = 2020 }, volume, issue).Score == 100
            && IntakeMatching.Rank(hints, volume, issue).Reason.Contains("cover year 2025 matches"), "Cover-date fallback or year-style volume failed.");
        Check(IntakeMatching.Rank(hints with { Volume = 2 }, volume, issue).Reason.Contains("ordinal unavailable"), "Series ID was mistaken for ordinal.");
        Check(IntakeMatching.Rank(hints with { Volume = 2 }, volume with { Ordinal = 2 }, issue).Score == 100, "Native volume ordinal did not rank.");
        Check(IntakeMatching.Rank(hints with { Year = 2024 }, volume, issue with { StoreDate = "2024-12-01" }).Score == 95, "Release year lost precedence over cover year.");
        foreach (var status in new[] { HttpStatusCode.TooManyRequests, HttpStatusCode.ServiceUnavailable })
        {
            var failing = new Handler(_ => new(status) { Content = new StringContent("check-secret-never-return") });
            using var provider = new Metron("check-secret-never-return", failing);
            await Error(() => provider.MatchIntakeAsync(hints), status == HttpStatusCode.TooManyRequests ? 429 : 502);
            if (status == HttpStatusCode.TooManyRequests)
            {
                await Error(() => provider.MatchIntakeAsync(hints), 429);
                Check(failing.Calls == 1, "Matching bypassed rate-limit cooldown.");
            }
        }
        using (var provider = new Metron("token", new Handler(_ => Json(new { count = 0, next = (string?)null, previous = (string?)null, results = Array.Empty<object>() }))))
        {
            var result = await provider.MatchIntakeAsync(hints with { Number = "" });
            Check(result.Candidates.Count == 0 && result.Warnings.Any(warning => warning.Contains("No candidates"))
                && result.Warnings.Any(warning => warning.Contains("No issue hint")), "Empty/broad search lacks explanation.");
        }
        using (var provider = new Metron("token", new Handler(_ => throw new Exception("Cancelled matching reached provider."))))
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

    private static object Volume(int id, string name, string publisher) => new { id, series = name + " (2020)", year_began = 2020,
        volume = 2, publisher = new { id = 1, name = publisher } };
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
