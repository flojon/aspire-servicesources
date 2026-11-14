namespace Aspire.Hosting.ServiceSources.Config;

internal enum BoundedReadStatus { Read, Missing, NotRegularFile, TooLarge }

/// <summary>Reads a small developer-owned file without following links or buffering an oversized one.</summary>
internal static class BoundedFile
{
    /// <summary>I/O failures other than a missing file propagate to the caller.</summary>
    public static BoundedReadStatus TryRead(string path, int maxBytes, out byte[] bytes)
    {
        bytes = [];

        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return BoundedReadStatus.Missing;
        }

        // A link is refused: renaming over it would sever it from what it points at.
        if (attributes.HasFlag(FileAttributes.Directory) || attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return BoundedReadStatus.NotRegularFile;
        }

        // FileShare.Delete so a concurrent save's rename over this name is not held hostage on Windows.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var buffer = new byte[maxBytes + 1];
        var length = 0;

        int read;
        while (length < buffer.Length && (read = stream.Read(buffer, length, buffer.Length - length)) > 0)
        {
            length += read;
        }

        if (length > maxBytes)
        {
            return BoundedReadStatus.TooLarge;
        }

        bytes = buffer[..length];
        return BoundedReadStatus.Read;
    }
}
