using System.Globalization;
using System.Text.Json;
using Npgsql;
using SubscriptionReminders.Business;
using SubscriptionReminders.Data;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Subscriptions")
    ?? throw new InvalidOperationException("ConnectionStrings:Subscriptions is required");
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<PostgresSubscriptionRepository>();
builder.Services.AddSingleton<ISubscriptionRepository>(sp => sp.GetRequiredService<PostgresSubscriptionRepository>());
builder.Services.AddSingleton<SubscriptionManager>();

var app = builder.Build();
await app.Services.GetRequiredService<PostgresSubscriptionRepository>().InitializeAsync();

app.MapGet("/services", () => Results.Json(new[]
{
    new { id = 1, name = "музыка" },
    new { id = 2, name = "видео" },
    new { id = 3, name = "облако" }
}));

app.MapGet("/subscriptions", async (SubscriptionManager manager) =>
    Results.Json(await manager.ListAsync()));

app.MapPost("/subscriptions", async (HttpRequest request, SubscriptionManager manager) =>
{
    var input = await ParseInputAsync(request);
    if (input is null) return Invalid("invalid subscription");
    try
    {
        var created = await manager.CreateAsync(input);
        return Results.Json(created, statusCode: 201);
    }
    catch (ArgumentException exception)
    {
        return Invalid(exception.Message);
    }
});

app.MapPut("/subscriptions/{id}", async (string id, HttpRequest request, SubscriptionManager manager) =>
{
    var input = await ParseInputAsync(request);
    if (input is null) return Invalid("invalid subscription");
    if (!TryId(id, out var parsedId)) return Invalid("invalid id");
    try
    {
        var updated = await manager.UpdateAsync(parsedId, input);
        return updated is null ? Results.Json(new { error = "subscription not found" }, statusCode: 404)
            : Results.Json(updated);
    }
    catch (ArgumentException exception)
    {
        return Invalid(exception.Message);
    }
});

app.MapDelete("/subscriptions/{id}", async (string id, SubscriptionManager manager) =>
{
    if (!TryId(id, out var parsedId)) return Invalid("invalid id");
    return await manager.DeleteAsync(parsedId)
        ? Results.NoContent()
        : Results.Json(new { error = "subscription not found" }, statusCode: 404);
});

app.MapGet("/reminders", async (string? date, SubscriptionManager manager) =>
{
    if (!TryDate(date, out var parsedDate)) return Invalid("invalid date");
    return Results.Json(await manager.GetRemindersAsync(parsedDate));
});

await app.RunAsync();

static IResult Invalid(string message) => Results.BadRequest(new { error = message });

static bool TryId(string text, out int id) =>
    int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;

static bool TryDate(string? text, out DateOnly date) =>
    DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

static async Task<SubscriptionInput?> ParseInputAsync(HttpRequest request)
{
    if (!request.HasJsonContentType()) return null;

    try
    {
        var body = await request.ReadFromJsonAsync<SubscriptionRequest>();
        if (body is null || !TryDate(body.NextPaymentDate, out var date)) return null;
        return new SubscriptionInput(body.ServiceId, body.AmountCents, date);
    }
    catch (JsonException)
    {
        return null;
    }
    catch (BadHttpRequestException)
    {
        return null;
    }
}

internal sealed record SubscriptionRequest(int ServiceId, int AmountCents, string? NextPaymentDate);
