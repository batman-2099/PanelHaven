using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PanelHaven.Web;

public sealed record ComicVineVolume(int Id, string Name, string StartYear, string Publisher, string Url);
public sealed record ComicVineIssue(int Id, string Name, string Number, string CoverDate, string Url)
{
    public string StoreDate { get; init; } = "";
    public string CoverUrl { get; init; } = "";
}
public sealed record ComicVinePage<T>(IReadOnlyList<T> Results, int Offset, int Total, int Limit = 20);
public sealed record ComicVineMetadata(Dictionary<string, string> Fields, string Url);
public sealed class ComicVineException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public sealed class ComicVine : IDisposable
{
    private const int MaxBody = 1024 * 1024;
    private readonly string apiKey;
    private readonly HttpClient client;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, (JsonElement Value, DateTimeOffset Expires)> cache = new(StringComparer.Ordinal);
    private DateTimeOffset nextRequest;
    private DateTimeOffset blockedUntil;
    public bool Configured => apiKey.Length != 0;

    public ComicVine(string? apiKey, HttpMessageHandler? handler = null)
    {
        this.apiKey = apiKey?.Trim() ?? "";
        // No HttpClientFactory logging: provider authentication is a query parameter.
        client = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            BaseAddress = new Uri("https://comicvine.gamespot.com/api/"),
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PanelHaven/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public async Task<ComicVinePage<ComicVineVolume>> VolumesAsync(string? query, int offset, CancellationToken cancellationToken = default)
    {
        query = query?.Trim();
        if (string.IsNullOrEmpty(query) || query.Length > 200 || query.Any(char.IsControl))
            throw new ComicVineException(400, "Enter a series name of 1–200 characters.");
        ValidateOffset(offset, 10);
        var root = await GetAsync($"search/?resources=volume&query={Uri.EscapeDataString(query)}&limit=10&offset={offset}&field_list=id,name,start_year,publisher,site_detail_url", cancellationToken);
        var results = Items(root, 10).EnumerateArray().Select(item => new ComicVineVolume(Id(item), Text(item, "name"),
            Text(item, "start_year"), Text(Property(item, "publisher"), "name"), Url(item))).ToArray();
        return new(results, offset, Integer(root, "number_of_total_results"), 10);
    }

    public async Task<ComicVinePage<ComicVineIssue>> IssuesAsync(int volumeId, string? number, int offset, CancellationToken cancellationToken = default)
    {
        ValidateId(volumeId);
        ValidateOffset(offset, 20);
        number = number?.Trim();
        if (number is { Length: > 40 } || number?.Any(c => char.IsControl(c) || c is ',' or ':' or '|' or '%' or '&' or '=' or '?' or '#') == true)
            throw new ComicVineException(400, "Issue number must be at most 40 characters and cannot contain filter separators.");
        var filter = $"volume:{volumeId}" + (string.IsNullOrEmpty(number) ? "" : $",issue_number:{number}");
        var root = await GetAsync($"issues/?filter={Uri.EscapeDataString(filter)}&limit=20&offset={offset}&field_list=id,name,issue_number,cover_date,store_date,image,site_detail_url", cancellationToken);
        var results = Items(root, 20).EnumerateArray().Select(item => new ComicVineIssue(Id(item), Text(item, "name"),
            Text(item, "issue_number"), Text(item, "cover_date"), Url(item)) { StoreDate = Text(item, "store_date"), CoverUrl = CoverUrl(item) }).ToArray();
        return new(results, offset, Integer(root, "number_of_total_results"));
    }

    public async Task<IntakeMatches> MatchIntakeAsync(IntakeHints? hints, CancellationToken cancellationToken = default)
    {
        hints = IntakeMatching.Validate(hints);
        var volumes = await VolumesAsync(hints.Series, 0, cancellationToken);
        var warnings = new List<string>();
        var candidates = new List<IntakeCandidate>();
        // ponytail: one search and at most three issue pages; pagination would require an explicit larger search budget.
        var selected = volumes.Results.DistinctBy(volume => volume.Id)
            .OrderByDescending(volume => IntakeMatching.SeriesScore(hints.Series, volume.Name)
                + (hints.Publisher.Length != 0 && IntakeMatching.SamePublisher(hints.Publisher, volume.Publisher) ? 10 : 0)
                + (hints.Volume is { } volumeHint && IntakeMatching.VolumeNumber(volume, volumeHint) == volumeHint ? 5 : 0))
            .ThenByDescending(volume => hints.Year is { } year && int.TryParse(volume.StartYear, out var start) && start <= year ? start : 0)
            .ThenBy(volume => volume.Id).Take(3).ToArray();
        if (volumes.Total > selected.Length)
            warnings.Add("Search is bounded to the first 10 volumes and the three closest series/publisher/volume matches. Refine the series and publisher hints for other volumes.");
        if (hints.Number.Length == 0)
            warnings.Add("No issue hint: only the first 20 issues per selected volume are considered. Enter an issue number to narrow the search.");
        if (hints.Volume is > 0 and < 1000)
            warnings.Add("Comic Vine does not expose a reliable volume ordinal. It can only be compared when explicitly present in the series name.");
        var number = hints.Number.Contains('/') || hints.Number == "½" ? hints.Number : IntakeMatching.NormalizeNumber(hints.Number);
        foreach (var volume in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var issues = await IssuesAsync(volume.Id, number, 0, cancellationToken);
            if (issues.Total > issues.Results.Count)
                warnings.Add($"Only the first 20 matching issues in volume {volume.Id} were considered.");
            candidates.AddRange(issues.Results.Select(issue => IntakeMatching.Rank(hints, volume, issue)));
        }
        var ranked = candidates.DistinctBy(candidate => candidate.IssueId).OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.VolumeId).ThenBy(candidate => candidate.IssueId).ToArray();
        if (ranked.Length == 0) warnings.Add("No candidates found. Refine the editable hints and retry; no metadata was changed.");
        else
        {
            if (ranked[0].Score < 70) warnings.Add("Low-confidence match: important hints are absent or disagree. Check every proposed field.");
            if (ranked.Length > 1 && ranked[0].Score - ranked[1].Score <= 5)
                warnings.Add("Ambiguous match: leading candidates have similar scores. Compare their series, dates and publisher before approving.");
        }
        return new(hints, ranked, warnings);
    }

    public async Task<ComicVineMetadata> MetadataAsync(int issueId, int volumeId, CancellationToken cancellationToken = default, bool intake = false)
    {
        ValidateId(issueId);
        ValidateId(volumeId);
        var issue = Property(await GetAsync($"issue/4000-{issueId}/?field_list=id,name,issue_number,volume,cover_date,store_date,description,deck,person_credits,character_credits,team_credits,location_credits,story_arc_credits,site_detail_url", cancellationToken), "results");
        if (Id(issue) != issueId || Id(Property(issue, "volume")) != volumeId)
            throw new ComicVineException(409, "The selected issue does not belong to this volume. Select the volume and issue again.");
        var url = Url(issue);
        JsonElement volume = default;
        await gate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow;
            // Reuse provider search fields; never trust client-supplied volume metadata.
            foreach (var entry in cache)
            {
                if (entry.Value.Expires <= now || !entry.Key.StartsWith("search/?resources=volume&", StringComparison.Ordinal)) continue;
                var results = Property(entry.Value.Value, "results");
                if (results.ValueKind != JsonValueKind.Array || results.GetArrayLength() > 10) continue;
                foreach (var item in results.EnumerateArray())
                {
                    var id = Property(item, "id");
                    if (id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var candidateId) || candidateId != volumeId
                        || Property(item, "name").ValueKind != JsonValueKind.String
                        || Property(item, "publisher").ValueKind == JsonValueKind.Undefined
                        || Property(item, "start_year").ValueKind == JsonValueKind.Undefined) continue;
                    volume = item;
                    break;
                }
                if (volume.ValueKind != JsonValueKind.Undefined) break;
            }
        }
        finally { gate.Release(); }
        if (volume.ValueKind == JsonValueKind.Undefined)
            volume = Property(await GetAsync($"volume/4050-{volumeId}/?field_list=id,name,publisher,start_year", cancellationToken), "results");
        if (Id(volume) != volumeId) throw InvalidResponse();
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string field, string value) { if (!string.IsNullOrWhiteSpace(value) && value.Length <= 16384) fields[field] = value; }
        Add("Series", Text(volume, "name"));
        Add("Publisher", Text(Property(volume, "publisher"), "name"));
        Add("Title", Text(issue, "name"));
        Add("Number", Text(issue, "issue_number"));
        if (intake && int.TryParse(Text(volume, "start_year"), out var volumeYear) && volumeYear is >= 1 and <= 9999)
            Add("Volume", volumeYear.ToString(CultureInfo.InvariantCulture));
        var summary = Text(issue, "description");
        Add("Summary", summary.Length == 0 ? Text(issue, "deck") : summary);
        Add("Web", url);
        var dateText = intake && DateOnly.TryParseExact(Text(issue, "store_date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            ? Text(issue, "store_date") : Text(issue, "cover_date");
        if (DateOnly.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            Add("Year", date.Year.ToString(CultureInfo.InvariantCulture));
            Add("Month", date.Month.ToString(CultureInfo.InvariantCulture));
            Add("Day", date.Day.ToString(CultureInfo.InvariantCulture));
        }
        foreach (var (source, field) in new[] { ("character_credits", "Characters"), ("team_credits", "Teams"), ("location_credits", "Locations"), ("story_arc_credits", "StoryArc") })
        {
            var credits = Property(issue, source);
            if (credits.ValueKind == JsonValueKind.Array)
                Add(field, string.Join(", ", credits.EnumerateArray().Select(item => Text(item, "name")).Where(name => name.Length != 0).Distinct(StringComparer.OrdinalIgnoreCase)));
        }
        var people = Property(issue, "person_credits");
        if (people.ValueKind == JsonValueKind.Array)
        {
            var credits = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var person in people.EnumerateArray())
            {
                var name = Text(person, "name");
                if (name.Length == 0) continue;
                foreach (var role in Text(person, "role").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    var field = role.ToLowerInvariant() switch
                    {
                        "writer" => "Writer", "penciler" or "penciller" => "Penciller", "inker" => "Inker",
                        "colorist" or "colourist" => "Colorist", "letterer" => "Letterer", "cover" or "cover artist" => "CoverArtist",
                        "editor" => "Editor", "translator" => "Translator", _ => null
                    };
                    if (field is null) continue;
                    if (!credits.TryGetValue(field, out var names)) credits[field] = names = [];
                    if (!names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
                }
            }
            foreach (var (field, names) in credits) Add(field, string.Join(", ", names));
        }
        return new(fields, url);
    }

    private async Task<JsonElement> GetAsync(string path, CancellationToken cancellationToken)
    {
        if (!Configured) throw new ComicVineException(503, "Comic Vine is not configured. Add your API key in Server settings, then restart the server.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var entered = false;
        try
        {
            await gate.WaitAsync(timeout.Token);
            entered = true;
            var now = DateTimeOffset.UtcNow;
            if (cache.TryGetValue(path, out var cached) && cached.Expires > now) return cached.Value;
            if (blockedUntil > now) throw new ComicVineException(429, "Comic Vine is rate limited. Wait before trying again.");
            if (nextRequest > now) await Task.Delay(nextRequest - now, timeout.Token);
            now = DateTimeOffset.UtcNow;
            nextRequest = now.AddSeconds(1);
            using var response = await client.GetAsync(path + "&format=json&api_key=" + Uri.EscapeDataString(apiKey), HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                blockedUntil = now.AddMinutes(1);
                var retry = response.Headers.RetryAfter;
                var retryAt = retry?.Date ?? (retry?.Delta is { } delta ? now.Add(delta) : blockedUntil);
                if (retryAt > blockedUntil) blockedUntil = retryAt;
                throw new ComicVineException(429, "Comic Vine is rate limited. Wait before trying again.");
            }
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new ComicVineException(502, "Comic Vine rejected access. Check the server API key and provider account.");
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new ComicVineException(404, "The Comic Vine entry was not found.");
            if (!response.IsSuccessStatusCode)
                throw new ComicVineException(502, "Comic Vine is unavailable. Try again later.");
            await response.Content.LoadIntoBufferAsync(MaxBody, timeout.Token);
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
            var root = document.RootElement;
            switch (Integer(root, "status_code"))
            {
                case 1: break;
                case 100: throw new ComicVineException(502, "Comic Vine rejected the API key. Check the key in Server settings and restart after changing it.");
                case 101: throw new ComicVineException(404, "The Comic Vine entry was not found.");
                case 107:
                    blockedUntil = now.AddMinutes(1);
                    throw new ComicVineException(429, "Comic Vine is rate limited. Wait before trying again.");
                default: throw new ComicVineException(502, "Comic Vine could not process the request. Try again later.");
            }
            if (Property(root, "results").ValueKind is not (JsonValueKind.Array or JsonValueKind.Object)) throw InvalidResponse();
            var value = root.Clone();
            if (cache.Count >= 32) cache.Remove(cache.MinBy(entry => entry.Value.Expires).Key);
            cache[path] = (value, DateTimeOffset.UtcNow.AddMinutes(5));
            return value;
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
            throw new ComicVineException(504, "Comic Vine took too long to respond. Try again.");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException)
        {
            // Never propagate upstream exception messages, URLs, bodies, or an inner exception containing the key.
            throw new ComicVineException(502, "Comic Vine returned an unavailable, oversized, or invalid response. Try again later.");
        }
        finally { if (entered) gate.Release(); }
    }

    private static void ValidateId(int id)
    {
        if (id <= 0) throw new ComicVineException(400, "Select a valid Comic Vine volume and issue.");
    }

    private static void ValidateOffset(int offset, int limit)
    {
        if (offset is < 0 or > 10000 || offset % limit != 0)
            throw new ComicVineException(400, $"Search offset must be a multiple of {limit} from 0 to 10000.");
    }

    private static ComicVineException InvalidResponse() => new(502, "Comic Vine returned an invalid response.");
    private static JsonElement Property(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) ? value : default;
    private static int Integer(JsonElement item, string name)
    {
        var value = Property(item, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number >= 0 ? number : throw InvalidResponse();
    }
    private static int Id(JsonElement item) => Integer(item, "id") is > 0 and var id ? id : throw InvalidResponse();
    private static JsonElement Items(JsonElement root, int limit)
    {
        var items = Property(root, "results");
        if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > limit) throw InvalidResponse();
        return items;
    }
    private static string Url(JsonElement item)
    {
        var value = Property(item, "site_detail_url");
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: <= 2048 } text
            || !Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || !uri.Host.Equals("comicvine.gamespot.com", StringComparison.OrdinalIgnoreCase) || !uri.IsDefaultPort
            || uri.UserInfo.Length != 0 || uri.AbsolutePath.StartsWith("/api", StringComparison.OrdinalIgnoreCase)) throw InvalidResponse();
        return uri.GetLeftPart(UriPartial.Path);
    }
    private static string CoverUrl(JsonElement item)
    {
        var value = Property(Property(item, "image"), "small_url");
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: <= 2048 } text
            || !Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || !uri.Host.Equals("comicvine.gamespot.com", StringComparison.OrdinalIgnoreCase) || !uri.IsDefaultPort
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || !uri.AbsolutePath.StartsWith("/a/uploads/", StringComparison.Ordinal)) return "";
        return uri.GetLeftPart(UriPartial.Path);
    }
    private static string Text(JsonElement item, string name)
    {
        var value = Property(item, name);
        if (value.ValueKind != JsonValueKind.String) return "";
        var text = Regex.Replace(value.GetString()!, @"<(script|style)\b[^>]*>.*?</(script|style)\s*>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.NonBacktracking);
        text = WebUtility.HtmlDecode(Regex.Replace(text, "<[^>]*>", " ", RegexOptions.NonBacktracking));
        text = Regex.Replace(text, @"\s+", " ", RegexOptions.NonBacktracking).Trim();
        try
        {
            System.Xml.XmlConvert.VerifyXmlChars(text);
            if (text.Length <= 16384) return text;
        }
        catch (System.Xml.XmlException) { }
        var clean = new StringBuilder(Math.Min(text.Length, 16384));
        for (var index = 0; index < text.Length && clean.Length < 16384; index++)
        {
            var character = text[index];
            if (char.IsHighSurrogate(character) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            {
                if (clean.Length + 2 > 16384) break;
                clean.Append(character).Append(text[++index]);
            }
            else if (System.Xml.XmlConvert.IsXmlChar(character)) clean.Append(character);
        }
        return clean.ToString().Trim();
    }

    public void Dispose() { client.Dispose(); gate.Dispose(); }
}
