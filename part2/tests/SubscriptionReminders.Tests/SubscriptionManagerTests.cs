using SubscriptionReminders.Business;

namespace SubscriptionReminders.Tests;

public sealed class SubscriptionManagerTests
{
    [Fact]
    public async Task Reminders_IncludeTodayAndThirdDay_ButExcludeOutsideDates()
    {
        var repository = new FakeRepository(
            new SubscriptionRecord(1, 1, 100, new DateOnly(2026, 11, 9)),
            new SubscriptionRecord(2, 1, 100, new DateOnly(2026, 11, 10)),
            new SubscriptionRecord(3, 2, 200, new DateOnly(2026, 11, 13)),
            new SubscriptionRecord(4, 3, 300, new DateOnly(2026, 11, 14)));
        var manager = new SubscriptionManager(repository);

        var reminders = await manager.GetRemindersAsync(new DateOnly(2026, 11, 10));

        Assert.Equal([
            new Reminder(2, 1, 100, new DateOnly(2026, 11, 10)),
            new Reminder(3, 2, 200, new DateOnly(2026, 11, 13))
        ], reminders);
    }

    [Fact]
    public async Task Reminders_AreSortedAcrossYearBoundary()
    {
        var manager = new SubscriptionManager(new FakeRepository(
            new SubscriptionRecord(3, 3, 300, new DateOnly(2027, 1, 2)),
            new SubscriptionRecord(2, 2, 200, new DateOnly(2026, 12, 31)),
            new SubscriptionRecord(1, 1, 100, new DateOnly(2026, 12, 31))));

        var reminders = await manager.GetRemindersAsync(new DateOnly(2026, 12, 30));

        Assert.Equal([1, 2, 3], reminders.Select(x => x.SubscriptionId).ToArray());
    }

    [Fact]
    public async Task Reminders_CrossMonthBoundary_AndReturnEmptyWhenNoneAreDue()
    {
        var manager = new SubscriptionManager(new FakeRepository(
            new SubscriptionRecord(1, 1, 100, new DateOnly(2026, 2, 28)),
            new SubscriptionRecord(2, 1, 200, new DateOnly(2026, 3, 1))));

        var reminders = await manager.GetRemindersAsync(new DateOnly(2026, 2, 27));
        Assert.Equal([1, 2], reminders.Select(x => x.SubscriptionId).ToArray());
        Assert.Empty(await manager.GetRemindersAsync(new DateOnly(2026, 3, 2)));
    }

    [Fact]
    public async Task List_SortsUnorderedRepositoryResults_AndPreservesMultipleSubscriptionsPerService()
    {
        var manager = new SubscriptionManager(new FakeRepository(
            new SubscriptionRecord(8, 1, 800, new DateOnly(2026, 11, 10)),
            new SubscriptionRecord(2, 1, 200, new DateOnly(2026, 11, 11))));

        var subscriptions = await manager.ListAsync();

        Assert.Equal([2, 8], subscriptions.Select(x => x.Id).ToArray());
        Assert.All(subscriptions, x => Assert.Equal(1, x.ServiceId));
    }

    [Fact]
    public async Task Create_RejectsNonPositiveAmountAndUnknownService()
    {
        var manager = new SubscriptionManager(new FakeRepository());
        var date = new DateOnly(2026, 11, 14);

        await Assert.ThrowsAsync<ArgumentException>(() => manager.CreateAsync(new(1, 0, date)));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.CreateAsync(new(1, -1, date)));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.CreateAsync(new(99, 100, date)));
        Assert.Empty(await manager.ListAsync());
    }

    [Fact]
    public async Task Update_RejectsInvalidInputWithoutChangingSavedSubscription()
    {
        var manager = new SubscriptionManager(new FakeRepository());
        var original = await manager.CreateAsync(new(1, 100, new DateOnly(2026, 11, 14)));

        await Assert.ThrowsAsync<ArgumentException>(() => manager.UpdateAsync(original.Id,
            new(1, 0, new DateOnly(2026, 11, 15))));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.UpdateAsync(original.Id,
            new(99, 100, new DateOnly(2026, 11, 15))));
        Assert.Equal(original, Assert.Single(await manager.ListAsync()));
    }

    [Fact]
    public async Task CreateUpdateDelete_AreVisibleThroughList()
    {
        var manager = new SubscriptionManager(new FakeRepository());
        var first = await manager.CreateAsync(new(1, 100, new DateOnly(2026, 11, 14)));
        var second = await manager.CreateAsync(new(2, 200, new DateOnly(2026, 11, 15)));

        var updated = await manager.UpdateAsync(first.Id, new(3, 300, new DateOnly(2026, 12, 1)));
        Assert.Equal(new SubscriptionRecord(first.Id, 3, 300, new DateOnly(2026, 12, 1)), updated);
        Assert.Equal([first.Id, second.Id], (await manager.ListAsync()).Select(x => x.Id).ToArray());
        Assert.True(await manager.DeleteAsync(second.Id));
        Assert.Single(await manager.ListAsync());
        Assert.False(await manager.DeleteAsync(second.Id));
        Assert.Null(await manager.UpdateAsync(second.Id, new(1, 100, new DateOnly(2026, 12, 1))));
    }

    private sealed class FakeRepository(params SubscriptionRecord[] records) : ISubscriptionRepository
    {
        private readonly List<SubscriptionRecord> _records = [.. records];
        private int _nextId = records.Length + 1;

        public Task<IReadOnlyList<SubscriptionRecord>> ListAsync() =>
            Task.FromResult<IReadOnlyList<SubscriptionRecord>>(_records.ToArray());
        public Task<bool> ServiceExistsAsync(int id) => Task.FromResult(id is >= 1 and <= 3);
        public Task<SubscriptionRecord> CreateAsync(SubscriptionInput input)
        {
            var created = new SubscriptionRecord(_nextId++, input.ServiceId, input.AmountCents, input.NextPaymentDate);
            _records.Add(created);
            return Task.FromResult(created);
        }
        public Task<SubscriptionRecord?> UpdateAsync(int id, SubscriptionInput input)
        {
            var index = _records.FindIndex(x => x.Id == id);
            if (index < 0) return Task.FromResult<SubscriptionRecord?>(null);
            var updated = new SubscriptionRecord(id, input.ServiceId, input.AmountCents, input.NextPaymentDate);
            _records[index] = updated;
            return Task.FromResult<SubscriptionRecord?>(updated);
        }
        public Task<bool> DeleteAsync(int id) => Task.FromResult(_records.RemoveAll(x => x.Id == id) != 0);
    }
}
