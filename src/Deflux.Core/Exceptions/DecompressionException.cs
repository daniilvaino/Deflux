using System;

namespace Deflux.Core.Exceptions;

public class DecompressionException : Exception
{
    public DecompressionException(string message) : base(message) { }
    public DecompressionException(string message, Exception innerException) : base(message, innerException) { }
}
