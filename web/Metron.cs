using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PanelHaven.Web;

public sealed class Metron : IMetadataProvider
{
    // Metron uses DRF PageNumberPagination with PAGE_SIZE=100 (not limit/offset).
    private const int PageSize = 100;
    private const int MaxBody = 1024 * 1024;
    private readonly HttpClient client;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, (JsonElement Value, DateTimeOffset Expires)> cache = new(StringComparer.Ordinal);
    private DateTimeOffset nextRequest;
    private DateTimeOffset blockedUntil;
    public string Id => "metron";
    public bool Configured { get; }

    public Metron(string? apiToken, HttpMessageHandler? handler = null)
    {
        apiToken = apiToken?.Trim() ?? "";
        if (apiToken.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
            throw new MetadataProviderException(400, "The Metron API token cannot contain whitespace or control characters.");
        Configured = apiToken.Length != 0;
        client = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            BaseAddress = new Uri("https://metron.cloud/api/"),
            Timeout = Timeout.InfiniteTimeSpan
        };
        if (Configured) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PanelHaven/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public async Task<MetadataPage<MetadataVolume>> VolumesAsync(string? query, int offset, CancellationToken cancellationToken = default)
    {
        query = query?.Trim();
        if (string.IsNullOrEmpty(query) || query.Length > 200 || query.Any(char.IsControl))
            throw new MetadataProviderException(400, "Enter a series name of 1–200 characters.");
        ValidateOffset(offset);
        var root = await GetAsync($"series/?name={Uri.EscapeDataString(query)}&page={offset / PageSize + 1}", cancellationToken);
        var results = Items(root).EnumerateArray().Select(item => Volume(item, list: true)).ToArray();
        return new(results, offset, Integer(root, "count"));
    }

    public async Task<MetadataPage<ProviderIssue>> IssuesAsync(int volumeId, string? number, int offset, CancellationToken cancellationToken = default)
    {
        ValidateId(volumeId);
        var root = await IssuePageAsync($"series_id={volumeId}", number, offset, cancellationToken);
        var results = Items(root).EnumerateArray().Select(item =>
        {
            if (ReadId(Property(item, "series")) != volumeId) throw InvalidResponse();
            return Issue(item);
        }).ToArray();
        return new(results, offset, Integer(root, "count"));
    }

    private Task<JsonElement> IssuePageAsync(string filter, string? number, int offset, CancellationToken cancellationToken)
    {
        ValidateOffset(offset);
        number = number?.Trim();
        if (number is { Length: > 40 } || number?.Any(c => char.IsControl(c) || c is ',' or ':' or '|' or '%' or '&' or '=' or '?' or '#') == true)
            throw new MetadataProviderException(400, "Issue number must be at most 40 characters and cannot contain filter separators.");
        return GetAsync($"issue/?{filter}" + (string.IsNullOrEmpty(number) ? "" : $"&number={Uri.EscapeDataString(number)}")
            + $"&page={offset / PageSize + 1}", cancellationToken);
    }

    public async Task<IntakeMatches> MatchIntakeAsync(IntakeHints? hints, CancellationToken cancellationToken = default)
    {
        hints = IntakeMatching.Validate(hints);
        var volumes = new Dictionary<int, MetadataVolume>();
        var filter = $"series_name={Uri.EscapeDataString(hints.Series)}";
        int? exactId = null;
        if (hints.Series.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(hints.Series, UriKind.Absolute, out var uri) || uri.Scheme != "https"
                || uri.Host != "metron.cloud" || !uri.IsDefaultPort || uri.UserInfo.Length != 0
                || uri.Query.Length != 0 || uri.Fragment.Length != 0
                || !Regex.IsMatch(uri.AbsolutePath, @"^/series/[1-9][0-9]*/$", RegexOptions.CultureInvariant)
                || !int.TryParse(uri.AbsolutePath.AsSpan(8, uri.AbsolutePath.Length - 9), NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                throw new MetadataProviderException(400, "Enter a Metron series URL with the path /series/series-id/.");
            var volume = await VolumeAsync(id, cancellationToken);
            volumes.Add(id, volume);
            exactId = id;
            hints = hints with { Series = volume.Name };
            filter = $"series_id={id}";
        }
        var number = hints.Number.Contains('/') || hints.Number == "½" ? hints.Number : IntakeMatching.NormalizeNumber(hints.Number);
        var root = await IssuePageAsync(filter, number, 0, cancellationToken);
        var items = Items(root);
        var series = new List<MetadataVolume>();
        if (exactId is null && items.GetArrayLength() != 0)
        {
            var missing = items.EnumerateArray().Select(item => ReadId(Property(item, "series"))).ToHashSet();
            for (var offset = 0; offset <= 10000 && missing.Count != 0; offset += PageSize)
            {
                var page = await VolumesAsync(hints.Series, offset, cancellationToken);
                foreach (var volume in page.Results)
                    if (missing.Remove(volume.Id)) series.Add(volume);
                if (page.Results.Count == 0 || offset + PageSize >= page.Total) break;
            }
        }
        var warnings = new List<string>();
        // ponytail: one 100-issue page bounds discovery; filter issues directly, never shortlist series first.
        if (Integer(root, "count") > items.GetArrayLength())
            warnings.Add("Search is bounded to the first 100 matching issues. Refine the series/issue hints or use an exact Metron series URL.");
        if (hints.Number.Length == 0)
            warnings.Add("No issue hint: only the first 100 matching issues are considered. Enter an issue number to narrow the search.");
        var candidates = new List<IntakeCandidate>();
        foreach (var item in items.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var volumeId = ReadId(Property(item, "series"));
            if (exactId is { } selected && volumeId != selected) throw InvalidResponse();
            if (!volumes.TryGetValue(volumeId, out var volume))
            {
                var listed = series.FirstOrDefault(candidate => candidate.Id == volumeId);
                volume = listed is null ? await VolumeAsync(volumeId, cancellationToken)
                    : Volume(Property(item, "series")) with { Publisher = listed.Publisher };
                volumes.Add(volumeId, volume);
            }
            candidates.Add(IntakeMatching.Rank(hints, volume, Issue(item)) with { Provider = Id });
        }
        var ranked = candidates.DistinctBy(candidate => candidate.IssueId).OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => int.TryParse(candidate.StartYear, out var start) && start is >= 1 and <= 9999 ? start : 0)
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

    private async Task<MetadataVolume> VolumeAsync(int id, CancellationToken cancellationToken)
    {
        var item = await GetAsync($"series/{id}/", cancellationToken);
        if (ReadId(item) != id) throw InvalidResponse();
        return Volume(item);
    }

    public async Task<MetadataProposal> MetadataAsync(int issueId, int volumeId, CancellationToken cancellationToken = default, bool intake = false)
    {
        ValidateId(issueId);
        ValidateId(volumeId);
        var issue = await GetAsync($"issue/{issueId}/", cancellationToken);
        if (ReadId(issue) != issueId || ReadId(Property(issue, "series")) != volumeId)
            throw new MetadataProviderException(409, "The selected issue does not belong to this series. Select the series and issue again.");
        var volume = Property(issue, "series");
        var url = WebUrl("issue", issueId);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string field, string value) { if (!string.IsNullOrWhiteSpace(value) && value.Length <= 16384) fields[field] = value; }
        Add("Series", Text(volume, "name"));
        Add("Publisher", Text(Property(issue, "publisher"), "name"));
        Add("Imprint", Text(Property(issue, "imprint"), "name"));
        Add("Title", Title(issue));
        Add("Number", Text(issue, "number"));
        Add("AlternateNumber", Text(issue, "alt_number"));
        if (intake) Add("Volume", PositiveNumber(volume, "year_began", 9999));
        var summary = Text(issue, "desc");
        var attribution = Property(issue, "attribution");
        if (summary.Length != 0 && attribution.ValueKind == JsonValueKind.Array)
        {
            var sources = attribution.EnumerateArray().Select(item =>
            {
                var source = Text(item, "source");
                var link = Text(item, "url");
                return Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme == "https"
                    && uri.UserInfo.Length == 0 && uri.IsDefaultPort ? $"{source} ({uri.AbsoluteUri})" : source;
            }).Where(source => source.Length != 0).Distinct(StringComparer.Ordinal);
            var credit = string.Join("; ", sources);
            if (credit.Length != 0) summary += "\n\nSources: " + credit;
        }
        Add("Summary", summary);
        Add("Web", url);
        Add("LanguageISO", Text(volume, "language"));
        Add("Format", Text(Property(volume, "series_type"), "name"));
        Add("AgeRating", Text(Property(issue, "rating"), "name"));
        Add("Genre", Names(Property(volume, "genres")));
        Add("PageCount", PositiveNumber(issue, "page", int.MaxValue));
        var dateText = intake && DateOnly.TryParseExact(Text(issue, "store_date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            ? Text(issue, "store_date") : Text(issue, "cover_date");
        if (DateOnly.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            Add("Year", date.Year.ToString(CultureInfo.InvariantCulture));
            Add("Month", date.Month.ToString(CultureInfo.InvariantCulture));
            Add("Day", date.Day.ToString(CultureInfo.InvariantCulture));
        }
        foreach (var (source, field) in new[] { ("characters", "Characters"), ("teams", "Teams"), ("arcs", "StoryArc") })
            Add(field, Names(Property(issue, source)));
        var people = Property(issue, "credits");
        if (people.ValueKind == JsonValueKind.Array)
        {
            var credits = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var person in people.EnumerateArray())
            {
                var name = Text(person, "creator");
                var roles = Property(person, "role");
                if (name.Length == 0 || roles.ValueKind != JsonValueKind.Array) continue;
                foreach (var role in roles.EnumerateArray())
                {
                    var field = Text(role, "name").ToLowerInvariant() switch
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
        if (!Configured) throw new MetadataProviderException(503, "Metron is not configured. Add your API token in Server settings, then restart the server.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var entered = false;
        try
        {
            await gate.WaitAsync(timeout.Token);
            entered = true;
            var now = DateTimeOffset.UtcNow;
            if (cache.TryGetValue(path, out var cached) && cached.Expires > now) return cached.Value;
            if (blockedUntil > now) throw new MetadataProviderException(429, "Metron is rate limited. Wait before trying again.");
            if (nextRequest > now) await Task.Delay(nextRequest - now, timeout.Token);
            now = DateTimeOffset.UtcNow;
            nextRequest = now.AddSeconds(3); // Metron's documented burst limit is 20 requests/minute.
            using var response = await client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                blockedUntil = now.AddMinutes(1);
                var retry = response.Headers.RetryAfter;
                var retryAt = retry?.Date ?? (retry?.Delta is { } delta ? now.Add(delta) : blockedUntil);
                if (retryAt > blockedUntil) blockedUntil = retryAt;
                throw new MetadataProviderException(429, "Metron is rate limited. Wait before trying again.");
            }
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new MetadataProviderException(502, "Metron rejected access. Check the server API token and provider account.");
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new MetadataProviderException(404, "The Metron entry was not found.");
            if (!response.IsSuccessStatusCode)
                throw new MetadataProviderException(502, "Metron is unavailable. Try again later.");
            await response.Content.LoadIntoBufferAsync(MaxBody, timeout.Token);
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw InvalidResponse();
            var value = document.RootElement.Clone();
            if (cache.Count >= 32) cache.Remove(cache.MinBy(entry => entry.Value.Expires).Key);
            cache[path] = (value, DateTimeOffset.UtcNow.AddMinutes(5));
            return value;
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
            throw new MetadataProviderException(504, "Metron took too long to respond. Try again.");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException)
        {
            // Never propagate upstream exception messages, URLs, bodies, or inner exceptions containing credentials.
            throw new MetadataProviderException(502, "Metron returned an unavailable, oversized, or invalid response. Try again later.");
        }
        finally { if (entered) gate.Release(); }
    }

    private static void ValidateId(int id)
    {
        if (id <= 0) throw new MetadataProviderException(400, "Select a valid Metron series and issue.");
    }
    private static void ValidateOffset(int offset)
    {
        if (offset is < 0 or > 10000 || offset % PageSize != 0)
            throw new MetadataProviderException(400, $"Search offset must be a multiple of {PageSize} from 0 to 10000.");
    }
    private static MetadataProviderException InvalidResponse() => new(502, "Metron returned an invalid response.");
    private static JsonElement Property(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) ? value : default;
    private static int Integer(JsonElement item, string name)
    {
        var value = Property(item, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number >= 0 ? number : throw InvalidResponse();
    }
    private static int ReadId(JsonElement item) => Integer(item, "id") is > 0 and var id ? id : throw InvalidResponse();
    private static string PositiveNumber(JsonElement item, string name, int max)
    {
        var value = Property(item, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number > 0 && number <= max
            ? number.ToString(CultureInfo.InvariantCulture) : "";
    }
    private static JsonElement Items(JsonElement root)
    {
        var items = Property(root, "results");
        if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > PageSize || Integer(root, "count") < items.GetArrayLength())
            throw InvalidResponse();
        // Never follow provider-supplied next/previous links: construct page requests on our fixed origin.
        return items;
    }
    private static string WebUrl(string resource, int id) => $"https://metron.cloud/{resource}/{id}/";
    private static MetadataVolume Volume(JsonElement item, bool list = false)
    {
        var id = ReadId(item);
        var ordinal = PositiveNumber(item, "volume", 9999);
        return new(id, Text(item, list ? "series" : "name"), PositiveNumber(item, "year_began", 9999),
            Text(Property(item, "publisher"), "name"), WebUrl("series", id))
        { Ordinal = int.TryParse(ordinal, out var number) ? number : null };
    }
    private static ProviderIssue Issue(JsonElement item)
    {
        var id = ReadId(item);
        return new(id, Text(item, "issue"), Text(item, "number"), Text(item, "cover_date"), WebUrl("issue", id))
        { StoreDate = Text(item, "store_date"), CoverUrl = CoverUrl(item) };
    }
    private static string CoverUrl(JsonElement item)
    {
        var value = Property(item, "image");
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: <= 2048 } text
            || !Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.Host is not ("static.metron.cloud" or "metron.cloud") || !uri.IsDefaultPort
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || !uri.AbsolutePath.StartsWith("/media/", StringComparison.Ordinal)) return "";
        return uri.AbsoluteUri;
    }
    private static string Names(JsonElement items) => items.ValueKind == JsonValueKind.Array
        ? string.Join(", ", items.EnumerateArray().Select(item => Text(item, "name")).Where(name => name.Length != 0).Distinct(StringComparer.OrdinalIgnoreCase)) : "";
    private static string Title(JsonElement item)
    {
        var title = Text(item, "title");
        var names = Property(item, "name");
        return title.Length != 0 ? title : names.ValueKind == JsonValueKind.Array
            ? string.Join("; ", names.EnumerateArray().Select(CleanText).Where(name => name.Length != 0).Distinct(StringComparer.OrdinalIgnoreCase)) : "";
    }
    private static string Text(JsonElement item, string name) => CleanText(Property(item, name));
    private static string CleanText(JsonElement value)
    {
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
