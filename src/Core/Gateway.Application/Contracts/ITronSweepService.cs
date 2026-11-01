using System.Threading;
using System.Threading.Tasks;

namespace Gateway.Application.Contracts;

public interface ITronSweepService
{
    Task<string> CreateUnsignedSweepTransactionAsync(string sourceAddress, string destinationAddress, long amountUnits, CancellationToken ct);
    Task<string> BroadcastSignedTransactionAsync(string signedTxJson, CancellationToken ct);
}