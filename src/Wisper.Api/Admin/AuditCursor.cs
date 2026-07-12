using System.Text;

namespace Wisper.Api.Admin;

/// <summary>
/// The opaque cursor that paginates the admin audit view (docs/API.md §8, §10). The <c>audit_log</c> id is a
/// monotonic <c>bigint</c> identity, so a keyset over it (return only rows with a smaller id — older —
/// newest first) can neither duplicate nor skip a row as new entries are appended during paging. The wire
/// form is URL-safe Base64 of the last id on the page; clients treat it as opaque.
/// </summary>
public static class AuditCursor
{
    /// <summary>Encodes the last id on a page to its opaque wire string.</summary>
    public static string Encode(long lastId) =>
        Base64Url(Encoding.UTF8.GetBytes(lastId.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>
    /// Parses an opaque cursor to the id bound it encodes. Returns <c>false</c> for any malformed token, so
    /// the caller can surface a <c>validation_error</c>.
    /// </summary>
    public static bool TryParse(string? raw, out long beforeId)
    {
        beforeId = 0;
        if (string.IsNullOrEmpty(raw) || !TryFromBase64Url(raw, out var bytes))
        {
            return false;
        }

        var text = Encoding.UTF8.GetString(bytes);
        return long.TryParse(
            text, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out beforeId) && beforeId > 0;
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
