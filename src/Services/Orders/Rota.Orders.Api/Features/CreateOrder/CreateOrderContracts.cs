using System.ComponentModel.DataAnnotations;

namespace Rota.Orders.Api.Features.CreateOrder;

/// <summary>
/// <c>POST /api/v1/orders</c> body (docs/05-contracts.md). Limits from docs/06 §3.
/// Coordinates are nullable so a missing field is "required", not a silent 0.
/// </summary>
public sealed record CreateOrderRequest(
    [Required, StringLength(64)] string MerchantOrderRef,
    [Required] LocationRequest Pickup,
    [Required] LocationRequest Dropoff,
    [Required] CustomerRequest Customer);

public sealed record LocationRequest(
    // Range rejects NaN too: NaN compares below every number.
    [Required, Range(-90d, 90d)] double? Lat,
    [Required, Range(-180d, 180d)] double? Lon,
    [Required, StringLength(300)] string Address);

public sealed record CustomerRequest(
    [Required, StringLength(100)] string Name,
    // E.164: '+', country code, up to 15 digits in total.
    [Required, RegularExpression(@"^\+[1-9][0-9]{1,14}$", ErrorMessage = "Phone must be in E.164 format, e.g. +905551112233.")]
    string Phone);

/// <summary>
/// 201 body. <see cref="DeliveryCode"/> appears only here, once; the database keeps its hash.
/// </summary>
public sealed record CreateOrderResponse(
    Guid Id,
    string Status,
    int DistanceMeters,
    string DeliveryCode,
    string TrackingUrl,
    int OrderVersion,
    DateTimeOffset CreatedAt);
