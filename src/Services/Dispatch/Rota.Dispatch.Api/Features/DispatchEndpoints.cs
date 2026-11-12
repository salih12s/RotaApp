using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Rota.BuildingBlocks.Primitives;
using Rota.BuildingBlocks.Web.Auth;
using Rota.BuildingBlocks.Web.Errors;
using Rota.Dispatch.Api.Domain;
using Rota.Dispatch.Api.Features.Jobs;
using Rota.Dispatch.Api.Features.Offering;
using Rota.Dispatch.Api.Features.Shifts;
using Rota.Dispatch.Api.Infrastructure;

namespace Rota.Dispatch.Api.Features;

public sealed record StartShiftRequest([StringLength(100)] string? DisplayName);

public sealed record ManualAssignRequest(Guid CourierId);

public sealed record OfferResponse(
    Guid Id, Guid OrderId, JobPlace Pickup, JobPlace Dropoff, int DistanceMeters, int EtaSeconds, DateTimeOffset ExpiresAt)
{
    public static OfferResponse From(OfferView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return new OfferResponse(view.OfferId, view.OrderId, view.Pickup, view.Dropoff, view.DistanceMeters, view.EtaSeconds, view.ExpiresAt);
    }
}

/// <summary>REST endpoints of docs/05 §1 "Dispatch". Roles are policies (403); ownership is part of the lookup (404).</summary>
public static class DispatchEndpoints
{
    public static WebApplication MapDispatchEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var api = app.MapGroup("/api/v1").WithTags("Dispatch").RequireAuthorization();

        api.MapPost("/couriers/me/shift/start", StartShiftAsync).RequireAuthorization(RotaPolicies.CourierOnly).WithName("StartShift");
        api.MapPost("/couriers/me/shift/end", EndShiftAsync).RequireAuthorization(RotaPolicies.CourierOnly).WithName("EndShift");
        api.MapGet("/couriers/me/offers", MyOffersAsync).RequireAuthorization(RotaPolicies.CourierOnly).WithName("MyOffers");
        api.MapPost("/offers/{id:guid}/accept", AcceptAsync).RequireAuthorization(RotaPolicies.CourierOnly).WithName("AcceptOffer");
        api.MapPost("/offers/{id:guid}/reject", RejectAsync).RequireAuthorization(RotaPolicies.CourierOnly).WithName("RejectOffer");

        api.MapGet("/couriers", ListCouriersAsync).RequireAuthorization(RotaPolicies.DispatcherOnly).WithName("ListCouriers");
        api.MapPost("/dispatch-jobs/{orderId:guid}/manual-assign", ManualAssignAsync).RequireAuthorization(RotaPolicies.DispatcherOnly).WithName("ManualAssign");
        api.MapPost("/dispatch-jobs/{orderId:guid}/restart", RestartAsync).RequireAuthorization(RotaPolicies.DispatcherOnly).WithName("RestartDispatchJob");

        return app;
    }

    private static async Task<IResult> StartShiftAsync(
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] StartShiftRequest? request,
        ClaimsPrincipal user, ShiftService shifts, CancellationToken cancellationToken) =>
        user.GetCourierId() is { } courierId
            ? ToResult(await shifts.StartAsync(courierId, request?.DisplayName, cancellationToken))
            : ProblemResults.NotFound();

    private static async Task<IResult> EndShiftAsync(ClaimsPrincipal user, ShiftService shifts, CancellationToken cancellationToken) =>
        user.GetCourierId() is { } courierId
            ? ToResult(await shifts.EndAsync(courierId, cancellationToken))
            : ProblemResults.NotFound();

    private static async Task<IResult> MyOffersAsync(ClaimsPrincipal user, DispatchDbContext db, CancellationToken cancellationToken)
    {
        if (user.GetCourierId() is not { } courierId)
        {
            return ProblemResults.NotFound();
        }

        var pending = await db.OfferViews(o => o.CourierId == courierId && o.Status == OfferStatus.Pending).ToListAsync(cancellationToken);
        return TypedResults.Ok(pending.Select(OfferResponse.From));
    }

    private static async Task<IResult> AcceptAsync(Guid id, ClaimsPrincipal user, OfferResponseService offers, CancellationToken cancellationToken) =>
        user.GetCourierId() is { } courierId
            ? ToResult(await offers.AcceptAsync(id, courierId, cancellationToken))
            : ProblemResults.NotFound();

    private static async Task<IResult> RejectAsync(Guid id, ClaimsPrincipal user, OfferResponseService offers, CancellationToken cancellationToken) =>
        user.GetCourierId() is { } courierId
            ? ToResult(await offers.RejectAsync(id, courierId, cancellationToken))
            : ProblemResults.NotFound();

    private static async Task<IResult> ListCouriersAsync(string? state, DispatchDbContext db, CancellationToken cancellationToken)
    {
        var couriers = db.Couriers.AsNoTracking();
        if (state is not null)
        {
            if (!Enum.TryParse<CourierState>(state, ignoreCase: false, out var parsed) || !Enum.IsDefined(parsed))
            {
                return new Error(ErrorCatalog.ValidationFailed, $"state must be one of: {string.Join(", ", Enum.GetNames<CourierState>())}.").ToProblem();
            }

            couriers = couriers.Where(c => c.State == parsed);
        }

        var found = await couriers.OrderBy(c => c.DisplayName).ThenBy(c => c.Id).ToListAsync(cancellationToken);
        return TypedResults.Ok(found.Select(CourierResponse.From));
    }

    private static async Task<IResult> ManualAssignAsync(
        Guid orderId, ManualAssignRequest request, JobCommandService jobs, CancellationToken cancellationToken)
    {
        if (request.CourierId == Guid.Empty)
        {
            return new Error(ErrorCatalog.ValidationFailed, "courierId is required.").ToProblem();
        }

        return ToResult(await jobs.AssignManuallyAsync(orderId, request.CourierId, cancellationToken));
    }

    private static async Task<IResult> RestartAsync(Guid orderId, JobCommandService jobs, CancellationToken cancellationToken)
    {
        var restarted = await jobs.RestartAsync(orderId, cancellationToken);
        return restarted.IsFailure ? restarted.Error.ToProblem() : TypedResults.Accepted((string?)null, restarted.Value);
    }

    private static IResult ToResult<T>(Result<T> result) =>
        result.IsFailure ? result.Error.ToProblem() : TypedResults.Ok(result.Value);
}
