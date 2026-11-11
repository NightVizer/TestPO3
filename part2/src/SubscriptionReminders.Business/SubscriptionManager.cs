namespace SubscriptionReminders.Business;

public sealed record SubscriptionInput(int ServiceId, int AmountCents, DateOnly NextPaymentDate);
public sealed record SubscriptionRecord(int Id, int ServiceId, int AmountCents, DateOnly NextPaymentDate);
public sealed record Reminder(int SubscriptionId, int ServiceId, int AmountCents, DateOnly NextPaymentDate);

public interface ISubscriptionRepository
{
    Task<bool> ServiceExistsAsync(int id);
    Task<IReadOnlyList<SubscriptionRecord>> ListAsync();
    Task<SubscriptionRecord> CreateAsync(SubscriptionInput input);
    Task<SubscriptionRecord?> UpdateAsync(int id, SubscriptionInput input);
    Task<bool> DeleteAsync(int id);
}

public sealed class SubscriptionManager(ISubscriptionRepository repository)
{
    public async Task<IReadOnlyList<SubscriptionRecord>> ListAsync() =>
        (await repository.ListAsync()).OrderBy(x => x.Id).ToArray();

    public async Task<SubscriptionRecord> CreateAsync(SubscriptionInput input)
    {
        await ValidateAsync(input);
        return await repository.CreateAsync(input);
    }

    public async Task<SubscriptionRecord?> UpdateAsync(int id, SubscriptionInput input)
    {
        await ValidateAsync(input);
        return await repository.UpdateAsync(id, input);
    }

    public Task<bool> DeleteAsync(int id) => repository.DeleteAsync(id);

    public async Task<IReadOnlyList<Reminder>> GetRemindersAsync(DateOnly date) =>
        (await repository.ListAsync())
            .Where(x => x.NextPaymentDate.DayNumber - date.DayNumber is >= 0 and <= 3)
            .OrderBy(x => x.NextPaymentDate)
            .ThenBy(x => x.Id)
            .Select(x => new Reminder(x.Id, x.ServiceId, x.AmountCents, x.NextPaymentDate))
            .ToArray();

    private async Task ValidateAsync(SubscriptionInput input)
    {
        if (input.AmountCents <= 0)
            throw new ArgumentException("amountCents must be positive");
        if (!await repository.ServiceExistsAsync(input.ServiceId))
            throw new ArgumentException("serviceId does not exist");
    }
}
