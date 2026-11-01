using System;

namespace Gateway.Domain.Entities;

public class BlockchainScanCursor
{
    public string Network { get; private set; } = string.Empty;
    public string Address { get; private set; } = string.Empty;
    public long LastProcessedBlockTimestamp { get; private set; }
    public ulong LastProcessedBlockNumber { get; private set; }
    public string? LastFingerprint { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    private BlockchainScanCursor() { }

    public BlockchainScanCursor(string network, string address, long initialTimestamp, ulong initialBlockNumber)
    {
        Network = network;
        Address = address;
        LastProcessedBlockTimestamp = initialTimestamp;
        LastProcessedBlockNumber = initialBlockNumber;
        LastFingerprint = null;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void UpdateCursor(long blockTimestamp, ulong blockNumber)
    {
        if (blockTimestamp >= LastProcessedBlockTimestamp)
        {
            LastProcessedBlockTimestamp = blockTimestamp;
            LastProcessedBlockNumber = Math.Max(LastProcessedBlockNumber, blockNumber);
            UpdatedAtUtc = DateTime.UtcNow;
        }
    }

    public void SetFingerprint(string? fingerprint)
    {
        LastFingerprint = fingerprint;
        UpdatedAtUtc = DateTime.UtcNow;
    }
}