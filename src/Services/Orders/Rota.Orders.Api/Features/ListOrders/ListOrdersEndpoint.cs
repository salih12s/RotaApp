using System.Buffers.Text;
using System.Globalization;
using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Rota.BuildingBlocks.Web.Auth;
using Rota.BuildingBlocks.Web.Errors;
using Rota.Orders.Api.Domain;
using Rota.Orders.Api.Infrastructure;

namespace Rota.Orders.Api.Features.ListOrders;

public sealed record OrderPage(IReadOnlyList<OrderResponse> Items, string? NextCursor);

/// <summary>
/// Keyset ("cursor") pagination, newest first. The cursor is the (created_at, id) of the last row
/// seen; the next page is "rows strictly before it". Unlike OFFSET, an order created meanwhile
/// cannot shift rows between pages, so nothing is repeated or skipped.
/// </summary>
public static class ListOrdersEndpoint
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 100;

    public static RouteGroupBuilder MapListOrders(this RouteGroupBuilder group)
    {
        group.MapGet("/", HandleAsync)
            .RequireAuthorization(RotaPolicies.MerchantOrDispatcher)
            .WithName("ListOrders")
            .Produces<OrderPage>()
            .ProducesValidationProblem();
        return group;
    }

    private static async Task<IResult> HandleAsync(
        string? status, int? limit, string? after, ClaimsPrincipal user, OrdersDbContext db, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        OrderStatus? statusFilter = null;
        if (status is not null)
        {
            if (Enum.TryParse<OrderStatus>(status, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
            {
                statusFilter = parsed;
            }
            else
            {
                errors["status"] = [$"Unknown status. Allowed: {string.Join(", ", Enum.GetNames<OrderStatus>())}."];
            }
        }

        var pageSize = limit ?? DefaultLimit;
        if (pageSize is < 1 or > MaxLimit)
        {
            errors["limit"] = [$"Must be between 1 and {MaxLimit}."];
        }

        Cursor? cursor = null;
        if (after is not null && !Cursor.TryDecode(after, out cursor))
        {
            errors["after"] = ["Not a cursor returned by this endpoint."];
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors, type: ErrorCatalog.TypeUri(ErrorCatalog.ValidationFailed));
        }

        var query = db.Orders.AsNoTracking().VisibleTo(user);
        if (statusFilter is { } s)
        {
            query = query.Where(o => o.Status == s);
        }

        if (cursor is not null)
        {
            // Row-value comparison: WHERE (created_at, id) < (@createdAt, @id), served by the index.
            var (createdAt, id) = (cursor.CreatedAt, cursor.Id);
            query = query.Where(o => EF.Functions.LessThan(ValueTuple.Create(o.CreatedAt, o.Id), ValueTuple.Create(createdAt, id)));
        }

        // One extra row tells whether another page exists, without a COUNT(*).
        var rows = await query
            .OrderByDescending(o => o.CreatedAt)
            .ThenByDescending(o => o.Id)
            .Take(pageSize + 1)
            .ToListAsync(cancellationToken);

        var page = rows.Take(pageSize).Select(OrderResponse.From).ToList();
        var next = rows.Count > pageSize ? new Cursor(page[^1].CreatedAt, page[^1].Id).Encode() : null;
        return TypedResults.Ok(new OrderPage(page, next));
    }

    /// <summary>Opaque to clients (base64url), so its format can change without breaking them.</summary>
    internal sealed record Cursor(DateTimeOffset CreatedAt, Guid Id)
    {
        public string Encode() =>
            Base64Url.EncodeToString(Encoding.UTF8.GetBytes(
                string.Create(CultureInfo.InvariantCulture, $"{CreatedAt.UtcTicks}:{Id:N}")));

        public static bool TryDecode(string value, out Cursor? cursor)
        {
            cursor = null;
            try
            {
                var text = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(value));
                var parts = text.Split(':');
                if (parts.Length == 2
                    && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                    && ticks is >= 0 and <= 3_155_378_975_999_999_999 // DateTime.MaxValue.Ticks
                    && Guid.TryParseExact(parts[1], "N", out var id))
                {
                    cursor = new Cursor(new DateTimeOffset(ticks, TimeSpan.Zero), id);
                    return true;
                }
            }
            catch (FormatException)
            {
                // Not base64url: falls through to "invalid cursor" (400), not a 500.
            }

            return false;
        }
    }
}
