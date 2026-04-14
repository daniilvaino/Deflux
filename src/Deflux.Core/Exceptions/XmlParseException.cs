using System;

namespace Deflux.Core.Exceptions;

public class XmlParseException : Exception
{
    public XmlParseException(string message) : base(message) { }
    public XmlParseException(string message, Exception innerException) : base(message, innerException) { }
}
