using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PanelHaven.Web;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record IntakeHints([property: JsonRequired] string Series, [property: JsonRequired] string Number,
    [property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int? Year,
    [property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int? Volume,
    [property: JsonRequired] string Publisher);
public sealed record IntakeBook(string BookId, string Source, IntakeHints Hints);
public sealed record IntakeCandidate(int VolumeId, int IssueId, string Series, string Number, string Title,
    string Publisher, string StartYear, string CoverDate, string Url, int Score, string Reason)
{
    public string CoverUrl { get; init; } = "";
}
public sealed record IntakeMatches(IntakeHints Hints, IReadOnlyList<IntakeCandidate> Candidates, IReadOnlyList<string> Warnings);

public static class IntakeMatching
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
    private const string VolumePattern = @"\b(?:v(?:ol(?:ume)?)?\.?)[ ]*(\d{1,4})\b";
    private const string YearPattern = @"\b((?:19|20)\d{2})\b";

    public static IntakeHints Validate(IntakeHints? hints)
    {
        if (hints is null || hints.Series is null || hints.Number is null || hints.Publisher is null
            || string.IsNullOrWhiteSpace(hints.Series) || hints.Series.Length > 200 || hints.Publisher.Length > 200
            || hints.Number.Length > 40 || hints.Year is < 1 or > 9999 || hints.Volume is < 1 or > 9999
            || hints.Series.Any(char.IsControl) || hints.Publisher.Any(char.IsControl)
            || hints.Number.Any(c => char.IsControl(c) || c is ',' or ':' or '|' or '%' or '&' or '=' or '?' or '#'))
            throw new ComicVineException(400, "Enter a series (1–200 characters), issue (up to 40 characters, without filter separators), publisher (up to 200 characters), and optional year/volume (1–9999).");
        return hints with { Series = hints.Series.Trim(), Number = hints.Number.Trim(), Publisher = hints.Publisher.Trim() };
    }

    public static IntakeHints Parse(string relativePath, IReadOnlyDictionary<string, string> metadata)
    {
        string Field(string name) => metadata.TryGetValue(name, out var value) ? value.Trim() : "";
        int? Integer(string value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            && number is >= 1 and <= 9999 ? number : null;
        var parts = relativePath.Replace('\\', '/').Split('/');
        var name = Path.GetFileNameWithoutExtension(parts[^1]).Replace('_', ' ');
        var volumeMatch = Regex.Match(name, VolumePattern, Options);
        var volume = volumeMatch.Success ? Integer(volumeMatch.Groups[1].Value) : Integer(Field("Volume"));
        name = Regex.Replace(name, VolumePattern, " ", Options);
        var yearMatch = Regex.Match(name, @"[\[(]((?:19|20)\d{2})[\])]", Options);
        var year = yearMatch.Success ? Integer(yearMatch.Groups[1].Value) : null;
        name = Regex.Replace(name, @"\([^)]*\)|\[[^\]]*\]", " ", Options).Trim();
        // A bare year is a hint only beside an issue, never the entire numeric series title (e.g. 2000 AD).
        var bareYear = Regex.Match(name, @"\s((?:19|20)\d{2})\s+(?:#?\d|Special\b|Annual\b)", Options);
        if (bareYear.Success)
        {
            year ??= Integer(bareYear.Groups[1].Value);
            name = name.Remove(bareYear.Groups[1].Index, bareYear.Groups[1].Length);
        }
        var trailingYear = Regex.Match(name, @"\s#?-?\d+(?:[./]\d+)?[a-z]*\s+((?:19|20)\d{2})\s*$", Options);
        if (trailingYear.Success)
        {
            year ??= Integer(trailingYear.Groups[1].Value);
            name = name.Remove(trailingYear.Groups[1].Index, trailingYear.Groups[1].Length).TrimEnd();
        }
        var issue = Regex.Match(name, @"(?:\s+#?|\s*#)(-?\d+(?:[./]\d+)?(?:[a-z]+)?|½|∞|nn|Special|Annual|FCBD|Alpha|Omega|Infinity)\s*$", Options);
        var number = issue.Success ? issue.Groups[1].Value : Field("Number");
        var series = issue.Success ? name[..issue.Index].Trim(' ', '-', '.') : name.Trim(' ', '-', '.');
        if (series.Length == 0 || !issue.Success && Field("Series").Length != 0) series = Field("Series");
        var publisher = Field("Publisher");
        // ponytail: nearest dated/weekly folder only; no arbitrary ancestor guesses or publisher directory catalog.
        for (var index = parts.Length - 2; index >= 0; index--)
        {
            var folder = parts[index].Replace('_', ' ');
            var folderYear = Regex.Match(folder, YearPattern, Options);
            if (!folderYear.Success || !Regex.IsMatch(folder,
                @"\b(?:week(?:ly)?|w\d{1,2})\b|\b\d{4}[-. ]\d{1,2}[-. ]\d{1,2}\b|\b\d{1,2}[-. ]\d{1,2}[-. ]\d{4}\b", Options)) continue;
            year ??= Integer(folderYear.Groups[1].Value);
            if (publisher.Length == 0)
            {
                var label = Regex.Replace(folder, @"\b(?:new comics?|week(?:ly)?|releases?|new)\b|\bw\d{1,2}\b|[\d\p{P}\p{S}]+", " ", Options);
                publisher = Regex.Replace(label, @"\s+", " ", Options).Trim();
                if (publisher.Length == 0 && index < parts.Length - 2
                    && Regex.IsMatch(parts[index + 1], @"^[\p{L}][\p{L} &.'-]{1,79}$", Options))
                    publisher = Regex.Replace(parts[index + 1], @"\s+week(?:ly)?$", "", Options).Trim();
            }
            break;
        }
        year ??= Integer(Field("Year"));
        string Bound(string value, int max) => new(value.Where(c => !char.IsControl(c)).Take(max).ToArray());
        return new(Bound(series, 200), Bound(number, 40), year, volume, Bound(publisher, 200));
    }

    public static string NormalizeNumber(string number)
    {
        number = number.Trim().ToUpperInvariant();
        if (number == "½") return "0.5";
        if (number == "INFINITY") return "∞";
        var fraction = number.Split('/');
        if (fraction.Length == 2 && decimal.TryParse(fraction[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var numerator)
            && decimal.TryParse(fraction[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var denominator) && denominator != 0)
            return (numerator / denominator).ToString("G29", CultureInfo.InvariantCulture);
        var match = Regex.Match(number, @"^(-?\d+(?:\.\d+)?)([a-z]*)$", Options);
        return match.Success && decimal.TryParse(match.Groups[1].Value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var value) ? value.ToString("G29", CultureInfo.InvariantCulture) + match.Groups[2].Value : number;
    }

    private static string NormalizeName(string text) => Regex.Replace(text.ToUpperInvariant(), @"[^\p{L}\p{N}]+", " ", Options).Trim();

    public static bool SamePublisher(string expected, string actual) =>
        Regex.Replace(NormalizeName(expected), @"\s+COMICS$", "", Options)
        == Regex.Replace(NormalizeName(actual), @"\s+COMICS$", "", Options);

    public static int SeriesScore(string expected, string actual)
    {
        var left = NormalizeName(Regex.Replace(expected, VolumePattern, " ", Options));
        var right = NormalizeName(Regex.Replace(actual, VolumePattern, " ", Options));
        if (left.Length == 0 || right.Length == 0) return 0;
        if (left == right) return 40;
        var words = left.Split(' ').ToHashSet(StringComparer.Ordinal);
        var other = right.Split(' ').ToHashSet(StringComparer.Ordinal);
        return 35 * words.Intersect(other).Count() / Math.Max(words.Count, other.Count);
    }

    public static int? VolumeNumber(ComicVineVolume volume, int hint)
    {
        var text = hint >= 1000 ? volume.StartYear : Regex.Match(volume.Name, VolumePattern, Options).Groups[1].Value;
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;
    }

    public static IntakeCandidate Rank(IntakeHints hints, ComicVineVolume volume, ComicVineIssue issue)
    {
        var score = SeriesScore(hints.Series, volume.Name);
        var reasons = new List<string> { score == 40 ? "Exact series" : $"Series similarity {score}/40" };
        if (hints.Number.Length != 0)
        {
            var same = NormalizeNumber(hints.Number) == NormalizeNumber(issue.Number);
            score += same ? 30 : 0;
            reasons.Add(same ? "issue matches" : "issue differs");
        }
        else reasons.Add("issue unspecified");
        if (hints.Year is { } year)
        {
            var storeDate = DateOnly.TryParseExact(issue.StoreDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date);
            if (storeDate || DateOnly.TryParseExact(issue.CoverDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            {
                var difference = Math.Abs(date.Year - year);
                score += difference == 0 ? 15 : difference == 1 ? 5 : 0;
                reasons.Add($"{(storeDate ? "release" : "cover")} year {date.Year}{(difference == 0 ? " matches" : " differs")}");
            }
            else reasons.Add("release year unavailable");
        }
        if (hints.Publisher.Length != 0)
        {
            var same = SamePublisher(hints.Publisher, volume.Publisher);
            score += same ? 10 : 0;
            reasons.Add(same ? "publisher matches" : "publisher differs or is unavailable");
        }
        if (hints.Volume is { } expectedVolume)
        {
            var actualVolume = VolumeNumber(volume, expectedVolume);
            var same = actualVolume == expectedVolume;
            score += same ? 5 : 0;
            reasons.Add(same ? "volume matches" : actualVolume is null ? "volume ordinal unavailable" : "volume differs");
        }
        return new(volume.Id, issue.Id, volume.Name, issue.Number, issue.Name, volume.Publisher, volume.StartYear,
            issue.CoverDate, issue.Url, score, string.Join("; ", reasons) + ". Review before approval; score is not a probability.") { CoverUrl = issue.CoverUrl };
    }
}

public sealed partial class LibraryStore
{
    public IntakeBook GetIntake(string id, bool requireUnsorted = true)
    {
        using var db = Open();
        using var command = Command(db, "SELECT availability FROM books WHERE id=$id", ("$id", id));
        var availability = command.ExecuteScalar() as string ?? throw new KeyNotFoundException();
        if (availability != "available")
            throw new ComicVineException(409, "This comicbook is unavailable. Rescan before reviewing it.");
        var source = GetSource(id);
        if (requireUnsorted && (unsortedPath is null || !IsContained(unsortedPath, source.Path)))
            throw new ComicVineException(409, "This comicbook is no longer in Unsorted. Refresh the review queue.");
        return new(id, FileDisplayPath(source.Path),
            IntakeMatching.Parse(Path.GetRelativePath(source.Root, source.Path), GetMetadata(id).Fields));
    }
}
