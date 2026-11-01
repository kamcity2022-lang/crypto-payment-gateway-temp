using System;
using System.Collections.Generic;

namespace Gateway.Domain.Entities;

public class Wallet
{
    public Guid Id { get; private set; }
    public Guid MerchantId { get; private set; }
    public string Network { get; private set; } = "TRON";
    public string AccountXPub { get; private set; } = string.Empty;
    public uint LastDerivationIndex { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    private readonly List<DerivedAddress> _derivedAddresses = new();
    public IReadOnlyCollection<DerivedAddress> DerivedAddresses => _derivedAddresses.AsReadOnly();

    private Wallet() { }

    public Wallet(Guid merchantId, string accountXPub, string network = "TRON")
    {
        Id = Guid.NewGuid();
        MerchantId = merchantId;
        AccountXPub = accountXPub;
        Network = network;
        LastDerivationIndex = 0;
        CreatedAtUtc = DateTime.UtcNow;
    }

    public DerivedAddress AllocateNextAddress(string base58Address)
    {
        var derived = new DerivedAddress(Id, LastDerivationIndex, base58Address, Network);
        _derivedAddresses.Add(derived);
        LastDerivationIndex++;
        return derived;
    }
}