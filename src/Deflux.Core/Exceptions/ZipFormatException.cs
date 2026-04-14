using System;

namespace Deflux.Core.Exceptions;

public class ZipFormatException : Exception
{
    public ZipFormatException(string message) : base(message) { }
    public ZipFormatException(string message, Exception innerException) : base(message, innerException) { }
}
