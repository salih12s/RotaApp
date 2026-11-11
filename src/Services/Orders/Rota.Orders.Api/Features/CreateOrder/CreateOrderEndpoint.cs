using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Rota.BuildingBlocks.Web.Auth;
using Rota.BuildingBlocks.Web.Errors;

namespace Rota.Orders.Api.Features.CreateOrder;

public static class CreateOrderEndpoint
{
    public const string IdempotencyKeyHeader = "Idempotency-Key";
    public const int MaxIdempotencyKeyLength = 128;

    public static RouteGroupBuilder MapCreateOrder(this RouteGroupBuilder group)
    {
        group.MapPost("/", HandleAsync)
            .RequireAuthorization(RotaPolicies.MerchantOnly)
            .WithName("CreateOrder")
            .Produces<CreateOrderResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        return group;
    }

    private static async Task<IResult> HandleAsync(
        CreateOrderRequest request,
        [FromHeader(Name = IdempotencyKeyHeader)] string? idempotencyKey,
        ClaimsPrincipal user,
        CreateOrderHandler handler,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(idempotencyKey))
        {
            return CreateOrderErrors.KeyMissing.ToProblem();
        }

        if (idempotencyKey.Length > MaxIdempotencyKeyLength)
        {
            return TypedResults.ValidationProblem(
                new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    [IdempotencyKeyHeader] = [$"Must be at most {MaxIdempotencyKeyLength} characters."],
                },
                type: ErrorCatalog.TypeUri(ErrorCatalog.ValidationFailed));
        }

        // MerchantOnly guarantees the role; every merchant token carries merchant_id.
        var merchantId = user.GetMerchantId()
            ?? throw new InvalidOperationException("A merchant principal without a merchant_id claim.");

        var result = await handler.HandleAsync(merchantId, idempotencyKey, request, cancellationToken);
        if (result.IsFailure)
        {
            return result.Error.ToProblem();
        }

        var reply = result.Value;
        return reply.StatusCode == StatusCodes.Status201Created
            ? TypedResults.Created($"/api/v1/orders/{reply.Body.Id}", reply.Body)
            : TypedResults.Json(reply.Body, statusCode: reply.StatusCode);
    }
}
