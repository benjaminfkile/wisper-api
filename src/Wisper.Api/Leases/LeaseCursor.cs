using System.Text;

namespace Wisper.Api.Leases;

/// <summary>
/// The opaque cursor that paginates a consumer's leases (docs/API.md §10). It encodes the stable sort
/// key of the last lease on the page — <c>(created_at, id)</c>, descending, matching the
/// <c>leases_consumer_idx</c> order (docs/DATA_MODEL.md §5) — so inserts during paging can neither
/// duplicate nor skip a lease. The wire form is URL-safe Base64 of <c>"{utcTicks}:{guid}"</c>; clients
/// treat it as an opaque token. Mirrors <see cref="Wisper.Api.Catalog.CatalogCursor"/>.
/// </summary>
public sealed record LeaseCursor(DateTimeOffset CreatedAt, Guid Id)
{
    /// <summary>Encodes this cursor to its opaque wire string.</summary>
    public string Encode()
    {
        var raw = $"{CreatedAt.UtcTicks}:{Id:N}";
        return Base64Url(Encoding.UTF8.GetBytes(raw));
    }

    /// <summary>
    /// Parses an opaque cursor string. Returns <c>false</c> (with a null <paramref name="cursor"/>) for
    /// any malformed token, so the caller can surface a <c>validation_error</c>.
    /// </summary>
    public static bool TryParse(string? raw, out LeaseCursor? cursor)
    {
        cursor = null;
        if (string.IsNullOrEmpty(raw))
        {
            return false;
        }

        if (!TryFromBase64Url(raw, out var bytes))
        {
            return false;
        }

        var text = Encoding.UTF8.GetString(bytes);
        var sep = text.IndexOf(':', StringComparison.Ordinal);
        if (sep <= 0)
        {
            return false;
        }

        if (!long.TryParse(text[..sep], out var ticks) ||
            !Guid.TryParseExact(text[(sep + 1)..], "N", out var id))
        {
            return false;
        }

        if (ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks)
        {
            return false;
        }

        cursor = new LeaseCursor(new DateTimeOffset(ticks, TimeSpan.Zero), id);
        return true;
    }

    /// <summary>
    /// Orders <c>(aCreatedAt, aId)</c> against <c>(bCreatedAt, bId)</c> by the stable descending key —
    /// newer <c>created_at</c> first, ties broken by the larger id. Negative when the first sorts before
    /// the second.
    /// </summary>
    public static int Compare(DateTimeOffset aCreatedAt, Guid aId, DateTimeOffset bCreatedAt, Guid bId)
    {
        var byTime = bCreatedAt.UtcTicks.CompareTo(aCreatedAt.UtcTicks);
        return byTime != 0 ? byTime : bId.CompareTo(aId);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryFromBase64Url(string value, out byte[] bytes)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
            case 1:
                bytes = Array.Empty<byte>();
                return false;
        }

        try
        {
            bytes = Convert.FromBase64String(padded);
            return true;
        }
        catch (FormatException)
        {
            bytes = Array.Empty<byte>();
            return false;
        }
    }
}
