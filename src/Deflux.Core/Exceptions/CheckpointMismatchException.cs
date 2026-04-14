using System;

namespace Deflux.Core.Exceptions;

public class CheckpointMismatchException : Exception
{
    public uint ExpectedCrc { get; }
    public uint ActualCrc { get; }

    public CheckpointMismatchException(string message) : base(message) { }

    public CheckpointMismatchException(string message, uint expectedCrc, uint actualCrc)
        : base(message)
    {
        ExpectedCrc = expectedCrc;
        ActualCrc = actualCrc;
    }
}
