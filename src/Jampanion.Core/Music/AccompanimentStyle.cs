namespace Jampanion.Core.Music;

public enum AccompanimentStyle
{
    Swing,
    JazzBallad,
    BossaNova,
    JazzWaltz,
    AfroCubanLatin
}

public static class AccompanimentStyleNames
{
    public static AccompanimentStyle Parse(string? value, string timeSignature = "4/4")
    {
        if (IsJazzWaltz(value) || string.Equals(timeSignature.Trim(), "3/4", StringComparison.Ordinal))
        {
            return AccompanimentStyle.JazzWaltz;
        }

        if (IsBossaNova(value))
        {
            return AccompanimentStyle.BossaNova;
        }

        if (IsJazzBallad(value))
        {
            return AccompanimentStyle.JazzBallad;
        }

        return IsAfroCubanLatin(value)
            ? AccompanimentStyle.AfroCubanLatin
            : AccompanimentStyle.Swing;
    }

    public static bool TryParseExplicit(string? value, out AccompanimentStyle style)
    {
        style = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        if (Enum.TryParse(normalized, ignoreCase: true, out style))
        {
            return true;
        }

        if (IsJazzWaltz(normalized))
        {
            style = AccompanimentStyle.JazzWaltz;
            return true;
        }

        if (IsBossaNova(normalized))
        {
            style = AccompanimentStyle.BossaNova;
            return true;
        }

        if (IsJazzBallad(normalized))
        {
            style = AccompanimentStyle.JazzBallad;
            return true;
        }

        if (IsAfroCubanLatin(normalized))
        {
            style = AccompanimentStyle.AfroCubanLatin;
            return true;
        }

        if (normalized.Contains("swing", StringComparison.OrdinalIgnoreCase))
        {
            style = AccompanimentStyle.Swing;
            return true;
        }

        return false;
    }

    public static string StorageName(AccompanimentStyle style) => style.ToString();

    public static bool IsBossaNova(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Contains("bossa", StringComparison.OrdinalIgnoreCase);

    public static bool IsJazzWaltz(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Contains("waltz", StringComparison.OrdinalIgnoreCase);

    public static bool IsJazzBallad(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        (value.Contains("ballad", StringComparison.OrdinalIgnoreCase) ||
         value.Contains("slow swing", StringComparison.OrdinalIgnoreCase));

    public static bool IsAfroCubanLatin(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Contains("latin", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("mambo", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("salsa", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("afro-cuban", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("afro cuban", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("montuno", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSwing(string? value) =>
        string.IsNullOrWhiteSpace(value) ||
        value.Contains("swing", StringComparison.OrdinalIgnoreCase);

    public static string DisplayName(AccompanimentStyle style) => style switch
    {
        AccompanimentStyle.BossaNova => "Bossa Nova",
        AccompanimentStyle.JazzBallad => "Jazz Ballad",
        AccompanimentStyle.JazzWaltz => "Jazz Waltz",
        AccompanimentStyle.AfroCubanLatin => "Latin / Mambo",
        _ => "Swing"
    };
}
