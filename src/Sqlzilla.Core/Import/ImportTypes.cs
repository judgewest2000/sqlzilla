using System.Globalization;
using System.Numerics;

namespace Sqlzilla.Import;

/// <summary>Guesses SQL types for imported text columns and converts text to values of a SQL type.</summary>
public static class ImportTypes
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static readonly string[] IsoFormats =
    [
        "yyyy-MM-dd", "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ss.FFFFFFF",
        "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFFFFFF",
    ];

    private static readonly string[] TimeFormats = [@"h\:mm", @"hh\:mm", @"h\:mm\:ss", @"hh\:mm\:ss", @"hh\:mm\:ss\.FFFFFFF"];

    private static readonly int[] StringSizes = [50, 100, 255, 500, 1000, 4000];

    /// <summary>The narrowest type that fits every non-empty value; nullable when any value is empty.</summary>
    public static (string SqlType, bool Nullable) Infer(IEnumerable<string?> values)
    {
        bool any = false, nullable = false;
        bool isBit = true, isInt = true, isBigint = true, isDecimal = true, isFloat = true, isGuid = true;
        bool isDate = true, isDateTime = true, isOffset = true, isTime = true;
        int maxLength = 0, intDigits = 1, scale = 0;

        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                nullable = true;
                continue;
            }
            any = true;
            maxLength = Math.Max(maxLength, value.Length);
            var s = value.Trim();
            // "007" is a code, not a number: keep the zeros.
            var number = !HasLeadingZero(s) && s.Any(char.IsAsciiDigit);

            isBit &= s.Equals("true", StringComparison.OrdinalIgnoreCase) || s.Equals("false", StringComparison.OrdinalIgnoreCase);
            isInt &= number && int.TryParse(s, NumberStyles.AllowLeadingSign, Inv, out _);
            isBigint &= number && long.TryParse(s, NumberStyles.AllowLeadingSign, Inv, out _);
            if (isDecimal &= number && decimal.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, Inv, out _))
            {
                var digits = s.TrimStart('-', '+');
                var dot = digits.IndexOf('.');
                intDigits = Math.Max(intDigits, dot < 0 ? digits.Length : dot);
                scale = Math.Max(scale, dot < 0 ? 0 : digits.Length - dot - 1);
            }
            isFloat &= number && double.TryParse(s, NumberStyles.Float, Inv, out _);
            isGuid &= s.Length >= 32 && Guid.TryParse(s, out _);
            isTime &= TimeSpan.TryParseExact(s, TimeFormats, Inv, out var time) && time < TimeSpan.FromDays(1);
            isOffset &= HasOffset(s) && DateTimeOffset.TryParse(s, Inv, DateTimeStyles.None, out _);
            if (isDateTime && TryParseDate(s, out var date))
                isDate &= date.TimeOfDay == TimeSpan.Zero && !s.Contains(':');
            else isDate = isDateTime = false;
        }

        if (!any) return ("nvarchar(50)", true);
        if (isBit) return ("bit", nullable);
        if (isInt) return ("int", nullable);
        if (isBigint) return ("bigint", nullable);
        if (isDecimal && intDigits + scale <= 38) return ($"decimal({Math.Clamp(intDigits + scale, 18, 38)},{scale})", nullable);
        if (isFloat) return ("float", nullable);
        if (isGuid) return ("uniqueidentifier", nullable);
        if (isTime) return ("time", nullable);
        if (isOffset) return ("datetimeoffset", nullable);
        if (isDate) return ("date", nullable);
        if (isDateTime) return ("datetime2", nullable);
        var size = StringSizes.FirstOrDefault(n => maxLength <= n);
        return (size == 0 ? "nvarchar(max)" : $"nvarchar({size})", nullable);
    }

    private static bool HasLeadingZero(string s)
    {
        var digits = s.StartsWith('-') || s.StartsWith('+') ? s[1..] : s;
        return digits.Length > 1 && digits[0] == '0' && char.IsAsciiDigit(digits[1]);
    }

    private static bool HasOffset(string s) =>
        s.EndsWith('Z') || (s.Length > 6 && s[^6] is '+' or '-' && s[^3] == ':' && s.Contains(':'));

    /// <summary>ISO formats first, then the user's culture (so 31/01/2024 works in the UK).</summary>
    private static bool TryParseDate(string s, out DateTime date)
    {
        if (DateTime.TryParseExact(s, IsoFormats, Inv, DateTimeStyles.None, out date)) return true;
        // Short strings like "1-2" parse as dates in some cultures but are usually codes.
        return s.Length >= 6 && s.Any(char.IsAsciiDigit)
               && DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.NoCurrentDateDefault, out date);
    }

    /// <summary>"nvarchar(50)" → "nvarchar".</summary>
    public static string BaseType(string sqlType)
    {
        var paren = sqlType.IndexOf('(');
        return (paren < 0 ? sqlType : sqlType[..paren]).Trim().ToLowerInvariant();
    }

    public static Type ClrType(string sqlType) => BaseType(sqlType) switch
    {
        "bit" => typeof(bool),
        "tinyint" => typeof(byte),
        "smallint" => typeof(short),
        "int" => typeof(int),
        "bigint" => typeof(long),
        "decimal" or "numeric" or "money" or "smallmoney" => typeof(decimal),
        "float" => typeof(double),
        "real" => typeof(float),
        "date" or "datetime" or "datetime2" or "smalldatetime" => typeof(DateTime),
        "datetimeoffset" => typeof(DateTimeOffset),
        "time" => typeof(TimeSpan),
        "uniqueidentifier" => typeof(Guid),
        "binary" or "varbinary" or "image" or "timestamp" or "rowversion" => typeof(byte[]),
        _ => typeof(string),
    };

    /// <summary>Converts text to a value for <paramref name="sqlType"/>; empty text is NULL. Throws <see cref="FormatException"/>.</summary>
    public static object Convert(string? text, string sqlType)
    {
        if (string.IsNullOrEmpty(text)) return DBNull.Value;
        var type = ClrType(sqlType);
        if (type == typeof(string)) return text;
        var s = text.Trim();
        if (s.Length == 0) return DBNull.Value;

        // No thousands separators: invariant parsing would read a European "1,5" as 15.
        const NumberStyles integer = NumberStyles.Integer;
        const NumberStyles real = NumberStyles.Float;
        object? result = type switch
        {
            _ when type == typeof(bool) => s.ToLowerInvariant() switch
            {
                "1" or "true" or "yes" or "y" => true,
                "0" or "false" or "no" or "n" => false,
                _ => null,
            },
            _ when type == typeof(byte) => Parse<byte>(s, integer),
            _ when type == typeof(short) => Parse<short>(s, integer),
            _ when type == typeof(int) => Parse<int>(s, integer),
            _ when type == typeof(long) => Parse<long>(s, integer),
            _ when type == typeof(decimal) => Parse<decimal>(s, real),
            _ when type == typeof(double) => Parse<double>(s, real),
            _ when type == typeof(float) => Parse<float>(s, real),
            _ when type == typeof(DateTime) => TryParseDate(s, out var d) ? d : null,
            _ when type == typeof(DateTimeOffset) => DateTimeOffset.TryParse(s, Inv, DateTimeStyles.AssumeLocal, out var o)
                || DateTimeOffset.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out o) ? o : null,
            _ when type == typeof(TimeSpan) => TimeSpan.TryParse(s, Inv, out var t) ? t
                : TryParseDate(s, out var dt) ? dt.TimeOfDay : null,
            _ when type == typeof(Guid) => Guid.TryParse(s, out var g) ? g : null,
            _ when type == typeof(byte[]) => TryHex(s),
            _ => null,
        };
        return result ?? throw new FormatException($"'{Shorten(text)}' is not a valid {sqlType}.");
    }

    private static object? Parse<T>(string s, NumberStyles styles) where T : struct, INumber<T> =>
        T.TryParse(s, styles, Inv, out var v) || T.TryParse(s, styles, CultureInfo.CurrentCulture, out v) ? (object)v : null;

    private static byte[]? TryHex(string s)
    {
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        try { return System.Convert.FromHexString(s); }
        catch (FormatException) { return null; }
    }

    private static string Shorten(string s) => s.Length <= 40 ? s : s[..40] + "…";
}
