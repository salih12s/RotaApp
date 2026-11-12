using Rota.BuildingBlocks.Primitives;

namespace Rota.Notifications.Api.Domain;

public enum DeliveryStatus
{
    Pending,
    Succeeded,
    Dead,
}

public static class NotificationErrors
{
    public static Error NotFound(string what) => new("not-found", $"The {what} was not found.");

    public static Error NotReplayable(DeliveryStatus current) =>
        new("invalid-delivery-state", $"Only dead deliveries can be replayed; this one is '{current}'.", new Dictionary<string, object?> { ["currentStatus"] = current.ToString() });

    public static Error ConcurrentChange { get; } = new("concurrency-conflict", "The resource was changed by another request; reload and retry.");
}

/// <summary>
/// One event for one merchant, on its way to the merchant's URL. <c>Pending</c> until a 2xx answer (<c>Succeeded</c>) or until the
/// seventh failure (<c>Dead</c>); a dispatcher-free replay by the merchant brings a dead one back to <c>Pending</c>.
/// </summary>
public sealed class WebhookDelivery
{
#pragma warning disable CS8618 // EF Core materialization only; every property is set from the row.
    private WebhookDelivery() { }
#pragma warning restore CS8618

    public Guid Id { get; private set; }

    public string MerchantId { get; private set; }

    public Guid EventId { get; private set; }

    public string EventType { get; private set; }

    public string Payload { get; private set; }

    public DeliveryStatus Status { get; private set; }

    public int AttemptCount { get; private set; }

    public DateTimeOffset? NextAttemptAt { get; private set; }

    public int? LastStatusCode { get; private set; }

    public string? LastError { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? DeliveredAt { get; private set; }

    public static WebhookDelivery Create(string merchantId, Guid eventId, string eventType, string payload, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(merchantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);

        return new WebhookDelivery
        {
            Id = Guid.CreateVersion7(now),
            MerchantId = merchantId,
            EventId = eventId,
            EventType = eventType,
            Payload = payload,
            Status = DeliveryStatus.Pending,
            NextAttemptAt = now,
            CreatedAt = now,
        };
    }

    public Result MarkSucceeded(int statusCode, DateTimeOffset now)
    {
        if (Status != DeliveryStatus.Pending)
        {
            return NotificationErrors.NotReplayable(Status);
        }

        AttemptCount++;
        Status = DeliveryStatus.Succeeded;
        LastStatusCode = statusCode;
        LastError = null;
        NextAttemptAt = null;
        DeliveredAt = now;
        return Result.Success();
    }

    /// <summary>The attempt failed (non-2xx, timeout, refused…): schedule the next one, or give up after the last.</summary>
    public Result MarkFailed(int? statusCode, string error, BackoffSchedule schedule, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        if (Status != DeliveryStatus.Pending)
        {
            return NotificationErrors.NotReplayable(Status);
        }

        AttemptCount++;
        LastStatusCode = statusCode;
        LastError = error.Length > 300 ? error[..300] : error;

        if (schedule.DelayAfter(AttemptCount) is { } delay)
        {
            NextAttemptAt = now + delay;
        }
        else
        {
            Status = DeliveryStatus.Dead;
            NextAttemptAt = null;
        }

        return Result.Success();
    }

    /// <summary>The merchant asks for a dead delivery again: a fresh start (attempt count 0), due now.</summary>
    public Result Replay(DateTimeOffset now)
    {
        if (Status != DeliveryStatus.Dead)
        {
            return NotificationErrors.NotReplayable(Status);
        }

        Status = DeliveryStatus.Pending;
        AttemptCount = 0;
        NextAttemptAt = now;
        LastError = null;
        return Result.Success();
    }
}

/// <summary>A merchant's webhook address and the (encrypted) key its deliveries are signed with. One per merchant.</summary>
public sealed class WebhookSubscription
{
#pragma warning disable CS8618 // EF Core materialization only; every property is set from the row.
    private WebhookSubscription() { }
#pragma warning restore CS8618

    public string MerchantId { get; private set; }

    public string Url { get; private set; }

    /// <summary>The secret, encrypted with ASP.NET Data Protection. The plain text is shown to the merchant once and never stored.</summary>
    public string SecretProtected { get; private set; }

    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static WebhookSubscription Create(string merchantId, string url, string secretProtected, DateTimeOffset now) => new()
    {
        MerchantId = merchantId,
        Url = url,
        SecretProtected = secretProtected,
        IsActive = true,
        CreatedAt = now,
        UpdatedAt = now,
    };

    public void ChangeUrl(string url, DateTimeOffset now)
    {
        Url = url;
        IsActive = true;
        UpdatedAt = now;
    }

    public void RotateSecret(string secretProtected, DateTimeOffset now)
    {
        SecretProtected = secretProtected;
        UpdatedAt = now;
    }
}

/// <summary>A courier's display name copied from <c>CourierStateChanged.v1</c> (data ownership: Notifications keeps its own copy).</summary>
public sealed class CourierName
{
#pragma warning disable CS8618 // EF Core materialization only; every property is set from the row.
    private CourierName() { }
#pragma warning restore CS8618

    public Guid CourierId { get; private set; }

    public string DisplayName { get; private set; }

    /// <summary>The Dispatch version this name came from; an older event never overwrites a newer one.</summary>
    public int Version { get; private set; }

    public static CourierName Create(Guid courierId, string displayName, int version) =>
        new() { CourierId = courierId, DisplayName = displayName, Version = version };
}
