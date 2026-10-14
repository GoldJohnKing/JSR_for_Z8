namespace Jsr4Z8.Core;

/// <summary>
/// A raw (NEF) file could not be read: missing, not a supported raw, or
/// corrupted (LibRaw open/unpack failure). Raised uniformly by
/// <see cref="RawMetadataReader.Read(string)"/> for all such causes.
/// </summary>
public sealed class RawReadException : Exception
{
    public RawReadException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
