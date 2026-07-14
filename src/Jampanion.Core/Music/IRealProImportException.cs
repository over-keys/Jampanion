namespace Jampanion.Core.Music;

public sealed class IRealProImportException : Exception
{
    public IRealProImportException(string message)
        : base(message)
    {
    }

    public IRealProImportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
