using System;

namespace Deflux.Core.Exceptions;

public class UnsupportedCompressionException : Exception
{
    public int CompressionMethod { get; }

    public UnsupportedCompressionException(int compressionMethod)
        : base($"Unsupported compression method: {compressionMethod}")
    {
        CompressionMethod = compressionMethod;
    }
}
