using System.Threading;
using System.Threading.Tasks;

namespace Gateway.Application.Contracts;

public interface ITronScannerService
{
    Task ScanPendingDepositsAsync(CancellationToken cancellationToken);
}