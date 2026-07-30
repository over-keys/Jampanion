using System.Text;

namespace Jampanion.Core.Music;

/// <summary>Shared new-song defaults and validation for desktop and Web.</summary>
public static class NewSongTemplate
{
    public const int MinimumBarCount = SessionConstants.BarsPerSegment;
    public const int MaximumBarCount = 512;
    public const int MaximumTitleLength = 120;
    public const int DefaultTempoBpm = 120;
    public const string DefaultTitle = "New Song";
    public const string DefaultKey = "C";
    public const string DefaultTimeSignature = "4/4";
    public const string DefaultChord = "C";

    public static void ValidateBarCount(int barCount)
    {
        if (barCount < MinimumBarCount || barCount > MaximumBarCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(barCount),
                $"A song must contain from {MinimumBarCount} to {MaximumBarCount} bars.");
        }
    }

    public static string NormalizeTitle(string? title)
    {
        var normalized = (title ?? string.Empty).Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Enter a song title.", nameof(title));
        }

        if (normalized.Length > MaximumTitleLength)
        {
            throw new ArgumentException(
                $"A song title cannot exceed {MaximumTitleLength} characters.",
                nameof(title));
        }

        if (normalized.IndexOfAny(new[] { '{', '}', '\r', '\n' }) >= 0)
        {
            throw new ArgumentException(
                "A song title cannot contain braces or a line break.",
                nameof(title));
        }

        return normalized;
    }

    public static string CreateChordPro(int barCount, string? id = null) =>
        CreateChordPro(barCount, DefaultTitle, id);

    public static string CreateChordPro(int barCount, string title, string? id)
    {
        ValidateBarCount(barCount);
        var normalizedTitle = NormalizeTitle(title);
        var builder = new StringBuilder();

        builder.AppendLine($"{{title: {normalizedTitle}}}");
        if (!string.IsNullOrWhiteSpace(id))
        {
            builder.AppendLine($"{{x-ai-jam-id: {id.Trim()}}}");
        }

        builder.AppendLine($"{{key: {DefaultKey}}}");
        builder.AppendLine($"{{time: {DefaultTimeSignature}}}");
        builder.AppendLine($"{{tempo: {DefaultTempoBpm}}}");
        builder.AppendLine(
            $"{{style: {AccompanimentStyleNames.StorageName(AccompanimentStyle.Swing)}}}");
        builder.AppendLine();
        builder.AppendLine("{start_of_grid}");

        for (var rowStart = 0;
             rowStart < barCount;
             rowStart += SessionConstants.BarsPerSegment)
        {
            builder.Append(rowStart == 0 ? "A " : "  ");
            var rowLength = Math.Min(
                SessionConstants.BarsPerSegment,
                barCount - rowStart);

            for (var offset = 0; offset < rowLength; offset++)
            {
                builder.Append("| ").Append(DefaultChord).Append(' ');
            }

            builder.AppendLine("|");
        }

        builder.AppendLine("{end_of_grid}");
        return builder.ToString();
    }
}
