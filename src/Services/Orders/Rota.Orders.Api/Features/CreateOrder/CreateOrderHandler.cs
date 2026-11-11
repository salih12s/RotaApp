using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Rota.BuildingBlocks.Messaging;
using Rota.BuildingBlocks.Persistence;
using Rota.BuildingBlocks.Primitives;
using Rota.Contracts.Events.Orders;
using Rota.Orders.Api.Domain;
using Rota.Orders.Api.Infrastructure;
using Rota.Orders.Api.Infrastructure.Configurations;

namespace Rota.Orders.Api.Features.CreateOrder;

public static class CreateOrderErrors
{
    public static Error KeyMissing { get; } =
        new("idempotency-key-missing", "The Idempotency-Key header is required.");

    public static Error KeyReused { get; } =
        new("idempotency-key-reused", "This Idempotency-Key was already used with a different request body.");

    public static Error DuplicateMerchantOrderRef { get; } =
        new("duplicate-merchant-order-ref", "An order with this merchantOrderRef already exists for the merchant.");
}

/// <summary>The HTTP outcome: the status and body to return (stored, so a retry gets the same).</summary>
public sealed record CreateOrderReply(int StatusCode, CreateOrderResponse Body);

/// <summary>
/// Creates an order exactly once per (merchant, Idempotency-Key).
/// The guard is the primary key of <c>idempotency_keys</c>, not a "does it exist?" query:
/// two concurrent first requests both see "no", but only one INSERT can win the key.
/// </summary>
public sealed partial class CreateOrderHandler(
    OrdersDbContext db,
    TimeProvider time,
    DeliveryCodeHasher hasher,
    ServiceAreaPolicy serviceArea,
    SecretProtector protector,
    TrackingTokenIssuer tracking,
    ILogger<CreateOrderHandler> logger)
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public async Task<Result<CreateOrderReply>> HandleAsync(
        string merchantId, string idempotencyKey, CreateOrderRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var requestHash = RequestHash.Compute(request);

        // Fast path for a sequential retry (the common case). Not the guard; see the class summary.
        var stored = await FindStoredAsync(merchantId, idempotencyKey, cancellationToken);
        if (stored is not null)
        {
            return Replay(stored, requestHash);
        }

        var now = time.GetUtcNow();
        var created = CreateOrder(merchantId, request, now);
        if (created.IsFailure)
        {
            return created.Error;
        }

        var (order, plainCode) = created.Value;
        var response = new CreateOrderResponse(
            order.Id, order.Status.ToString(), order.DistanceMeters, plainCode,
            tracking.CreateTrackingUrl(order.Id, now), order.Version, order.CreatedAt);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // 1) The key first. A concurrent request with the same key blocks on this primary key
            //    until we commit, then gets a unique violation and replays our stored response.
            db.IdempotencyKeys.Add(new IdempotencyKey
            {
                MerchantId = merchantId,
                Key = idempotencyKey,
                RequestHash = requestHash,
                ResponseStatus = StatusCodes.Status201Created,
                ResponseBody = JsonSerializer.Serialize(Protect(response), _json),
                CreatedAt = now,
            });
            await db.SaveChangesAsync(cancellationToken);

            // 2) The order, its first history row and the event: same transaction, all or nothing.
            db.Orders.Add(order);
            db.AddToOutbox(ToEvent(order), order.Id, order.Version, now);
            await db.SaveChangesAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation(IdempotencyKeyConfiguration.PrimaryKeyName))
        {
            // Lost the race: the winner has committed (that is what we waited for). Replay it.
            await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            LogConcurrentDuplicate(logger, merchantId);

            var winner = await FindStoredAsync(merchantId, idempotencyKey, cancellationToken)
                ?? throw new InvalidOperationException("Unique violation on the idempotency key, but no stored key was found.");
            return Replay(winner, requestHash);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation(OrderConfiguration.MerchantOrderRefIndex))
        {
            // A different key for an order number the merchant already used: a client bug, not a retry.
            await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            return CreateOrderErrors.DuplicateMerchantOrderRef;
        }

        return new CreateOrderReply(StatusCodes.Status201Created, response);
    }

    private Result<NewOrder> CreateOrder(string merchantId, CreateOrderRequest request, DateTimeOffset now)
    {
        // The validation filter has already rejected nulls and out-of-range values.
        var pickup = GeoPoint.Create(request.Pickup.Lat!.Value, request.Pickup.Lon!.Value);
        var dropoff = GeoPoint.Create(request.Dropoff.Lat!.Value, request.Dropoff.Lon!.Value);
        if (pickup.IsFailure || dropoff.IsFailure)
        {
            return OrderErrors.InvalidCoordinates;
        }

        return Order.Create(
            Guid.CreateVersion7(now),
            merchantId,
            request.MerchantOrderRef,
            new OrderLocation(pickup.Value, request.Pickup.Address),
            new OrderLocation(dropoff.Value, request.Dropoff.Address),
            new CustomerContact(request.Customer.Name, request.Customer.Phone),
            serviceArea,
            hasher,
            now);
    }

    private Task<IdempotencyKey?> FindStoredAsync(string merchantId, string key, CancellationToken cancellationToken) =>
        db.IdempotencyKeys.AsNoTracking()
            .SingleOrDefaultAsync(k => k.MerchantId == merchantId && k.Key == key, cancellationToken);

    private Result<CreateOrderReply> Replay(IdempotencyKey stored, string requestHash)
    {
        if (!string.Equals(stored.RequestHash, requestHash, StringComparison.Ordinal))
        {
            return CreateOrderErrors.KeyReused;
        }

        var body = JsonSerializer.Deserialize<CreateOrderResponse>(stored.ResponseBody, _json)
            ?? throw new InvalidOperationException("Stored idempotent response is empty.");
        return new CreateOrderReply(stored.ResponseStatus, Unprotect(body));
    }

    // The order id is the associated data: a ciphertext moved onto another row does not decrypt.
    private CreateOrderResponse Protect(CreateOrderResponse response) => response with
    {
        DeliveryCode = protector.Protect(response.DeliveryCode, response.Id.ToString()),
        TrackingUrl = protector.Protect(response.TrackingUrl, response.Id.ToString()),
    };

    private CreateOrderResponse Unprotect(CreateOrderResponse response) => response with
    {
        DeliveryCode = protector.Unprotect(response.DeliveryCode, response.Id.ToString()),
        TrackingUrl = protector.Unprotect(response.TrackingUrl, response.Id.ToString()),
    };

    private static OrderCreatedV1 ToEvent(Order order) => new(
        order.Id,
        order.MerchantId,
        order.MerchantOrderRef,
        new LocationV1(order.PickupLocation.Point.Latitude, order.PickupLocation.Point.Longitude, order.PickupLocation.Address),
        new LocationV1(order.DropoffLocation.Point.Latitude, order.DropoffLocation.Point.Longitude, order.DropoffLocation.Address),
        order.DistanceMeters,
        order.CreatedAt,
        order.Version);

    [LoggerMessage(Level = LogLevel.Information, Message = "Concurrent duplicate create for merchant {MerchantId}; replaying the stored response")]
    private static partial void LogConcurrentDuplicate(ILogger logger, string merchantId);
}
