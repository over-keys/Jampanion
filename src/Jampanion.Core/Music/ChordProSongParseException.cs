namespace Jampanion.Core.Music;

public sealed class ChordProSongParseException : FormatException
{
    public ChordProSongParseException(string message, int lineNumber = 0, int barNumber = 0, Exception? innerException = null)
        : base(FormatMessage(message, lineNumber, barNumber), innerException)
    {
        LineNumber = lineNumber;
        BarNumber = barNumber;
    }

    public int LineNumber { get; }
    public int BarNumber { get; }

    private static string FormatMessage(string message, int lineNumber, int barNumber)
    {
        var location = barNumber > 0
            ? $"Bar {barNumber}"
            : lineNumber > 0 ? $"Line {lineNumber}" : string.Empty;
        return location.Length == 0 ? message : $"{location}: {message}";
    }
}
