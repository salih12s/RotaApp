using Rota.BuildingBlocks.Web;

var builder = WebApplication.CreateBuilder(args);

builder.AddRotaServiceDefaults();
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseRotaServiceDefaults();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.Run();
