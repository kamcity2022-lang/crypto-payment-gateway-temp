using System.Threading;
using System.Threading.Tasks;

namespace Gateway.Application.Contracts;

public interface IWebhookDispatcher
{
    Task DispatchPendingWebhooksAsync(CancellationToken cancellationToken);
}