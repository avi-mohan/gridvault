using NodaTime;
using NodaTime.Text;

namespace GridVault.Ingestion.ObjectStorage;

/// <summary>
/// The key format under which a raw payload is landed in object storage:
/// {source}/{report}/{fetchedAt}/{originalFileName}. FetchedAt is embedded
/// so it can be decoded back out at load time as the transaction_time
/// fallback for reports that don't carry their own publish timestamp — see
/// docs/decisions.md. It is read once, at fetch time, and never
/// recomputed.
/// </summary>
public sealed record RawPayloadKey(string Source, string Report, Instant FetchedAt, string OriginalFileName)
{
    private static readonly InstantPattern FetchedAtPattern =
        InstantPattern.CreateWithInvariantCulture("yyyyMMdd'T'HHmmss'Z'");

    public string Format() => $"{Source}/{Report}/{FetchedAtPattern.Format(FetchedAt)}/{OriginalFileName}";

    public static bool TryParse(string key, out RawPayloadKey? parsed)
    {
        var segments = key.Split('/');
        if (segments.Length != 4)
        {
            parsed = null;
            return false;
        }

        var fetchedAtResult = FetchedAtPattern.Parse(segments[2]);
        if (!fetchedAtResult.Success)
        {
            parsed = null;
            return false;
        }

        parsed = new RawPayloadKey(segments[0], segments[1], fetchedAtResult.Value, segments[3]);
        return true;
    }
}
