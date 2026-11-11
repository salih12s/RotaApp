using Rota.BuildingBlocks.Web;

var builder = WebApplication.CreateBuilder(args);

builder.AddRotaServiceDefaults();

var app = builder.Build();

app.UseRotaServiceDefaults();

app.Run();
