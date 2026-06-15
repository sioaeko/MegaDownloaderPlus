namespace MegaDownloaderNext.Core.Mega;

public sealed class MegaApiException : Exception
{
    public MegaApiException(string message)
        : base(message)
    {
    }

    public MegaApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

