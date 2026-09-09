using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexHud.Core;

/// <summary>One immutable ledger observation and its numeric cursor state; contains no message bodies.</summary>
public sealed record UsageLedgerCheckpoint(string Epoch, TokenCounts Counts, DateTimeOffset ObservedAt,
    string Generation, long Sequence)
{
    public const string CurrentScope = "local-workload/main-pool/guardian-review-excluded/v1";
    public int Version { get; init; } = 1;
    public string HomeHash { get; init; } = "";
    public string Scope { get; init; } = CurrentScope;
    public string SchemaHash { get; init; } = "";
    public DateTimeOffset Started { get; init; }
    public IReadOnlyList<UsageLedgerCursorCheckpoint> Cursors { get; init; } = Array.Empty<UsageLedgerCursorCheckpoint>();
    public IReadOnlyDictionary<string, string> HistoricalUnavailable { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, DateTimeOffset> Profiles { get; init; } = new Dictionary<string, DateTimeOffset>();
    public IReadOnlyDictionary<string, DateTimeOffset> AmbiguousPools { get; init; } = new Dictionary<string, DateTimeOffset>();
    public string Checksum { get; init; } = "";

    public bool HasValidChecksum()
    {
        try { return IsHash(Checksum) && string.Equals(Checksum, ComputeChecksum(), StringComparison.Ordinal); }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or NotSupportedException) { return false; }
    }

    internal UsageLedgerCheckpoint Seal() => this with { Checksum = ComputeChecksum() };
    private string ComputeChecksum() => Hash(JsonSerializer.Serialize(this with { Checksum = "" }));
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    internal static bool IsId(string? value) => value is { Length: > 0 and <= 256 } && !value.Any(char.IsControl);
    internal static bool ValidCounts(TokenCounts? counts) => counts is not null
        && counts.InputTokens is >= 0 && counts.CachedInputTokens is >= 0 && counts.CacheWriteInputTokens is >= 0
        && counts.OutputTokens is >= 0 && counts.ReasoningOutputTokens is >= 0 && counts.TotalTokens is >= 0
        && counts.CachedInputTokens <= counts.InputTokens && counts.CacheWriteInputTokens <= counts.InputTokens
        && counts.ReasoningOutputTokens <= counts.OutputTokens
        && (decimal)counts.InputTokens.Value + counts.OutputTokens.Value == counts.TotalTokens.Value;
}

public sealed record UsageLedgerCursorCheckpoint(string ThreadId, string Path, string Identity, long Offset, string Anchor)
{
    public TokenCounts? Cumulative { get; init; }
    public DateTimeOffset? LastTokenTime { get; init; }
    public string Profile { get; init; } = "model:?/effort:?/tier:?";
    public string IndexProfile { get; init; } = "model:?/effort:?/tier:?";
    public string IndexProvider { get; init; } = "?";
    public bool HasUsage { get; init; }
    public IReadOnlyDictionary<string, string> TurnProfiles { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> ResponseOrder { get; init; } = Array.Empty<string>();
}