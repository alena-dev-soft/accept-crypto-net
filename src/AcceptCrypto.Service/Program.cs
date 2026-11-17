using AcceptCrypto.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();
builder.Services.AddAcceptCryptoPersistence(builder.Configuration);

var app = builder.Build();

if (app.Services.GetRequiredService<IOptions<PersistenceOptions>>().Value.MigrateOnStartup)
{
    await app.Services.MigrateAcceptCryptoDatabaseAsync();
}

app.MapHealthChecks("/health");
app.MapGet("/", () => Results.Text("AcceptCrypto.NET service is running. See /health."));

await app.RunAsync();
