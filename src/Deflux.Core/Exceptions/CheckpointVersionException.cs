using System;

namespace Deflux.Core.Exceptions;

public class CheckpointVersionException : Exception
{
    public byte ExpectedVersion { get; }
    public byte ActualVersion { get; }

    public CheckpointVersionException(byte expectedVersion, byte actualVersion)
        : base($"Checkpoint version mismatch: expected {expectedVersion}, got {actualVersion}")
    {
        ExpectedVersion = expectedVersion;
        ActualVersion = actualVersion;
    }
}
