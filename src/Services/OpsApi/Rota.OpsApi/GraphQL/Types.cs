using Rota.OpsApi.Infrastructure;

namespace Rota.OpsApi.GraphQL;

/// <summary>The <c>Order</c> type of docs/05 §5: flat columns become <c>id</c>, <c>pickup</c> and <c>dropoff</c>; one computed field.</summary>
public sealed class OrderType : ObjectType<OrderView>
{
    protected override void Configure(IObjectTypeDescriptor<OrderView> descriptor)
    {
        descriptor.Name("Order");
        descriptor.Field(o => o.OrderId).Name("id");

        // Plumbing that is not part of the panel's contract.
        descriptor.Field(o => o.CourierId).Ignore();
        descriptor.Field(o => o.PickedUpAt).Ignore();
        descriptor.Field(o => o.OrderVersion).Ignore();

        descriptor.Field(o => o.PickupLat).Ignore();
        descriptor.Field(o => o.PickupLon).Ignore();
        descriptor.Field(o => o.DropoffLat).Ignore();
        descriptor.Field(o => o.DropoffLon).Ignore();
        descriptor.Field("pickup").Type<NonNullType<ObjectType<GeoPoint>>>().Resolve(c =>
        {
            var o = c.Parent<OrderView>();
            return new GeoPoint(o.PickupLat, o.PickupLon);
        });
        descriptor.Field("dropoff").Type<NonNullType<ObjectType<GeoPoint>>>().Resolve(c =>
        {
            var o = c.Parent<OrderView>();
            return new GeoPoint(o.DropoffLat, o.DropoffLon);
        });

        descriptor.Field("courier").Type<CourierType>().Resolve(async (c, ct) =>
            c.Parent<OrderView>().CourierId is { } id ? await c.Services.GetRequiredService<CourierByIdLoader>().LoadAsync(id, ct) : null);

        // "How long until a courier was found": null while nobody is assigned.
        descriptor.Field("timeToAssignSeconds").Type<IntType>().Resolve(c =>
        {
            var o = c.Parent<OrderView>();
            return o.AssignedAt is { } assigned ? (int)(assigned - o.CreatedAt).TotalSeconds : (int?)null;
        });
    }
}

public sealed class CourierType : ObjectType<CourierView>
{
    protected override void Configure(IObjectTypeDescriptor<CourierView> descriptor)
    {
        descriptor.Name("Courier");
        descriptor.Field(c => c.CourierId).Name("id");
        // The property holds the order id; the field of the same name is the Order itself, loaded in one batch.
        descriptor.Field(c => c.CurrentOrder).Type<OrderType>().Resolve(async (c, ct) =>
            c.Parent<CourierView>().CurrentOrder is { } id ? await c.Services.GetRequiredService<OrderByIdLoader>().LoadAsync(id, ct) : null);
        descriptor.Field("lastLocation").Type<ObjectType<CourierLocationInfo>>().Resolve(async (c, ct) => await c.Services.GetRequiredService<LastLocationLoader>().LoadAsync(c.Parent<CourierView>().CourierId, ct));
        descriptor.Field(c => c.UpdatedAt).Ignore();
        descriptor.Field(c => c.CourierVersion).Ignore();
    }
}

/// <summary>What the panel may filter orders by (docs/05 §5 <c>OrderFilterInput</c>). Explicit: a new column is not filterable until someone decides it should be.</summary>
public sealed class OrderFilterInputType : HotChocolate.Data.Filters.FilterInputType<OrderView>
{
    protected override void Configure(HotChocolate.Data.Filters.IFilterInputTypeDescriptor<OrderView> descriptor)
    {
        descriptor.Name("OrderFilterInput");
        descriptor.BindFieldsExplicitly();
        descriptor.Field(o => o.OrderId).Name("id");
        descriptor.Field(o => o.MerchantId);
        descriptor.Field(o => o.MerchantOrderRef);
        descriptor.Field(o => o.Status);
        descriptor.Field(o => o.CreatedAt);
        descriptor.Field(o => o.AssignedAt);
        descriptor.Field(o => o.DeliveredAt);
    }
}

public sealed class OrderSortInputType : HotChocolate.Data.Sorting.SortInputType<OrderView>
{
    protected override void Configure(HotChocolate.Data.Sorting.ISortInputTypeDescriptor<OrderView> descriptor)
    {
        descriptor.Name("OrderSortInput");
        descriptor.BindFieldsExplicitly();
        descriptor.Field(o => o.MerchantId);
        descriptor.Field(o => o.Status);
        descriptor.Field(o => o.CreatedAt);
        descriptor.Field(o => o.AssignedAt);
        descriptor.Field(o => o.DeliveredAt);
    }
}
