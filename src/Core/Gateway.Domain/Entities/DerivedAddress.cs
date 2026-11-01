using System;

namespace Gateway.Domain.Entities;

public class DerivedAddress
{
    public Guid Id { get; private set; }
    public Guid WalletId { get; private set; }
    public uint AddressIndex { get; private set; }
    public string Address { get; private set; } = string.Empty;
    public string Network { get; private set; } = "TRON";
    public bool IsAllocated { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    private DerivedAddress() { }

    public DerivedAddress(Guid walletId, uint addressIndex, string address, string network)
    {
        Id = Guid.NewGuid();
        WalletId = walletId;
        AddressIndex = addressIndex;
        Address = address;
        Network = network;
        IsAllocated = true;
        CreatedAtUtc = DateTime.UtcNow;
    }
}