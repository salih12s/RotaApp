using HotChocolate.Authorization;
using Rota.BuildingBlocks.Web.Auth;
using Rota.OpsApi.Infrastructure;

namespace Rota.OpsApi.GraphQL;

/// <summary>
/// The dispatcher's two commands (docs/05 §5). Both go to Dispatch, which owns the jobs and couriers; this API never writes them itself. A refusal that
/// the dispatcher can act on comes back as a typed error in the payload (<c>errors</c>); Dispatch being down is a plain GraphQL error. The panel does not
/// see the result in its lists until the event comes back through the read model, so the payload carries Dispatch's own answer.
/// </summary>
public sealed class Mutation
{
    /// <summary>Gives the order to a courier by hand. Dispatch decides: the courier must be available, the job not finished.</summary>
    [Authorize(Policy = RotaPolicies.DispatcherOnly)]
    [Error<CourierNotAvailableException>]
    [Error<JobNotAssignableException>]
    [Error<OrderNotFoundException>]
    public Task<DispatchJob> AssignCourierAsync(Guid orderId, Guid courierId, [Service] DispatchClient dispatch, CancellationToken cancellationToken) =>
        dispatch.AssignCourierAsync(orderId, courierId, cancellationToken);

    /// <summary>Searches again for an order whose job ended unassigned.</summary>
    [Authorize(Policy = RotaPolicies.DispatcherOnly)]
    [Error<JobNotAssignableException>]
    [Error<OrderNotFoundException>]
    public Task<DispatchJob> RestartDispatchAsync(Guid orderId, [Service] DispatchClient dispatch, CancellationToken cancellationToken) =>
        dispatch.RestartAsync(orderId, cancellationToken);
}
