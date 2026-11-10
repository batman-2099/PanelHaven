namespace PanelHaven.Web;

public sealed record MetadataVolume(int Id, string Name, string StartYear, string Publisher, string Url)
{
    public int? Ordinal { get; init; }
}
public sealed record ProviderIssue(int Id, string Name, string Number, string CoverDate, string Url)
{
    public string StoreDate { get; init; } = "";
    public string CoverUrl { get; init; } = "";
}
public sealed record MetadataPage<T>(IReadOnlyList<T> Results, int Offset, int Total, int Limit = 100);
public sealed record MetadataProposal(Dictionary<string, string> Fields, string Url);
public sealed class MetadataProviderException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public interface IMetadataProvider : IDisposable
{
    string Id { get; }
    bool Configured { get; }
    Task<MetadataPage<MetadataVolume>> VolumesAsync(string? query, int offset, CancellationToken cancellationToken = default);
    Task<MetadataPage<ProviderIssue>> IssuesAsync(int volumeId, string? number, int offset, CancellationToken cancellationToken = default);
    Task<IntakeMatches> MatchIntakeAsync(IntakeHints? hints, CancellationToken cancellationToken = default);
    Task<MetadataProposal> MetadataAsync(int issueId, int volumeId, CancellationToken cancellationToken = default, bool intake = false);
}
