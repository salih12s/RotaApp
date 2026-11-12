using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Rota.BuildingBlocks.Persistence;
using Rota.BuildingBlocks.Primitives;
using Rota.BuildingBlocks.Web.Auth;
using Rota.BuildingBlocks.Web.Errors;
using Rota.Notifications.Api.Domain;
using Rota.Notifications.Api.Infrastructure;

namespace Rota.Notifications.Api.Features;

public sealed record SubscriptionRequest(string? Url);

/// <summary><c>secret</c> is present only when it was just created or rotated: it is shown once and cannot be read back.</summary>
public sealed record SubscriptionResponse(string Url, bool IsActive, string? Secret);

public sealed record DeliveryResponse(
    Guid Id, Guid EventId, string EventType, string Status, int AttemptCount, int? LastStatusCode, string? LastError,
    DateTimeOffset CreatedAt, DateTimeOffset? DeliveredAt, DateTimeOffset? NextAttemptAt)
{
    public static DeliveryResponse From(WebhookDelivery d)
    {
        ArgumentNullException.ThrowIfNull(d);
        return new DeliveryResponse(d.Id, d.EventId, d.EventType, d.Status.ToString(), d.AttemptCount, d.LastStatusCode, d.LastError,
            d.CreatedAt, d.DeliveredAt, d.NextAttemptAt);
    }
}

public sealed record DeliveryPage(IReadOnlyList<DeliveryResponse> Items, string? NextCursor);

/// <summary>
/// The merchant's own webhook settings and delivery history (docs/05 §1 "Notifications"). Everything is scoped to the caller's
/// <c>merchant_id</c> inside the query: another merchant's delivery is a 404, exactly like one that does not exist.
/// </summary>
public static class WebhookEndpoints
{
    private const int DefaultLimit = 20;
    private const int MaxLimit = 100;

    public static WebApplication MapWebhookEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var api = app.MapGroup("/api/v1").WithTags("Notifications").RequireAuthorization(RotaPolicies.MerchantOnly);
        api.MapPut("/webhook-subscription", PutSubscriptionAsync).WithName("PutWebhookSubscription");
        api.MapPost("/webhook-subscription/rotate-secret", RotateSecretAsync).WithName("RotateWebhookSecret");
        api.MapGet("/webhook-deliveries", ListDeliveriesAsync).WithName("ListWebhookDeliveries");
        api.MapPost("/webhook-deliveries/{id:guid}/replay", ReplayAsync).WithName("ReplayWebhookDelivery");
        return app;
    }

    private static async Task<IResult> PutSubscriptionAsync(
        SubscriptionRequest request, ClaimsPrincipal user, NotificationsDbContext db, WebhookUrlValidator validator,
        SecretProtector protector, TimeProvider time, CancellationToken cancellationToken)
    {
        var merchantId = user.GetMerchantId()!;
        var url = await validator.ValidateAsync(request.Url, cancellationToken);
        if (url.IsFailure)
        {
            return url.Error.ToProblem();
        }

        var now = time.GetUtcNow();
        var existing = await db.Subscriptions.SingleOrDefaultAsync(s => s.MerchantId == merchantId, cancellationToken);
        string? secret = null;
        if (existing is null)
        {
            secret = SecretProtector.NewSecret();
            existing = WebhookSubscription.Create(merchantId, url.Value.AbsoluteUri, protector.Protect(secret), now);
            db.Subscriptions.Add(existing);
        }
        else
        {
            existing.ChangeUrl(url.Value.AbsoluteUri, now);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            // Two first PUTs at the same moment: one created the row, this one lost. Retrying then updates it.
            return NotificationErrors.ConcurrentChange.ToProblem();
        }

        return TypedResults.Ok(new SubscriptionResponse(existing.Url, existing.IsActive, secret));
    }

    private static async Task<IResult> RotateSecretAsync(
        ClaimsPrincipal user, NotificationsDbContext db, SecretProtector protector, TimeProvider time, CancellationToken cancellationToken)
    {
        var merchantId = user.GetMerchantId()!;
        var subscription = await db.Subscriptions.SingleOrDefaultAsync(s => s.MerchantId == merchantId, cancellationToken);
        if (subscription is null)
        {
            return ProblemResults.NotFound();
        }

        var secret = SecretProtector.NewSecret();
        subscription.RotateSecret(protector.Protect(secret), time.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(new SubscriptionResponse(subscription.Url, subscription.IsActive, secret));
    }

    private static async Task<IResult> ListDeliveriesAsync(
        string? status, int? limit, string? after, ClaimsPrincipal user, NotificationsDbContext db, CancellationToken cancellationToken)
    {
        var merchantId = user.GetMerchantId()!;

        DeliveryStatus? statusFilter = null;
        if (status is not null)
        {
            if (!Enum.TryParse<DeliveryStatus>(status, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            {
                return Invalid($"status must be one of: {string.Join(", ", Enum.GetNames<DeliveryStatus>())}.");
            }

            statusFilter = parsed;
        }

        var take = limit ?? DefaultLimit;
        if (take is < 1 or > MaxLimit)
        {
            return Invalid($"limit must be between 1 and {MaxLimit}.");
        }

        Guid? cursor = null;
        if (after is not null)
        {
            if (!Guid.TryParse(after, out var parsedCursor))
            {
                return Invalid("after must be the nextCursor of a previous page.");
            }

            cursor = parsedCursor;
        }

        var query = db.Deliveries.AsNoTracking().Where(d => d.MerchantId == merchantId);
        if (statusFilter is { } wanted)
        {
            query = query.Where(d => d.Status == wanted);
        }

        if (cursor is { } last)
        {
            query = query.Where(d => d.Id.CompareTo(last) < 0);
        }

        // Ids are time-ordered (version 7), so "newest first" is "id descending"; one extra row says whether there is another page.
        var rows = await query.OrderByDescending(d => d.Id).Take(take + 1).ToListAsync(cancellationToken);
        var page = rows.Take(take).Select(DeliveryResponse.From).ToList();
        return TypedResults.Ok(new DeliveryPage(page, rows.Count > take ? page[^1].Id.ToString("D") : null));
    }

    private static async Task<IResult> ReplayAsync(
        Guid id, ClaimsPrincipal user, NotificationsDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        var merchantId = user.GetMerchantId()!;
        var now = time.GetUtcNow();

        // Only a dead delivery of this merchant moves; the condition is the guard, so two replays at once change it once.
        var changed = await db.Deliveries
            .Where(d => d.Id == id && d.MerchantId == merchantId && d.Status == DeliveryStatus.Dead)
            .ExecuteUpdateAsync(set => set
                .SetProperty(d => d.Status, DeliveryStatus.Pending)
                .SetProperty(d => d.AttemptCount, 0)
                .SetProperty(d => d.NextAttemptAt, now)
                .SetProperty(d => d.LastError, (string?)null), cancellationToken);
        if (changed == 1)
        {
            return TypedResults.Accepted((string?)null, new { id });
        }

        var current = await db.Deliveries.AsNoTracking().Where(d => d.Id == id && d.MerchantId == merchantId)
            .Select(d => (DeliveryStatus?)d.Status).SingleOrDefaultAsync(cancellationToken);
        return current is null ? ProblemResults.NotFound() : NotificationErrors.NotReplayable(current.Value).ToProblem();
    }

    private static IResult Invalid(string message) => new Error(ErrorCatalog.ValidationFailed, message).ToProblem();
}
