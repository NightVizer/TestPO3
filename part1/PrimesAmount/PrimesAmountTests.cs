using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using async_tests;

namespace async_tests.Tests;

[TestFixture]
public class PrimesAmountTests
{
    private sealed class StubPrimeService : PrimeService
    {
        private readonly Func<int, bool> _check;
        private int _clearCount;

        public StubPrimeService(Func<int, bool> check) => _check = check;
        public int ClearCount => Volatile.Read(ref _clearCount);
        public override bool CheckIsPrime(int value) => _check(value);
        public override void ClearCache() => Interlocked.Increment(ref _clearCount);
    }

    private static bool IsPrime(int value) =>
        value >= 2 && Enumerable.Range(2, Math.Max(0, value - 2))
            .All(divisor => divisor > value / divisor || value % divisor != 0);

    [Test]
    public void CountPrimesAmount_ReturnsEachPrimeRegardlessOfWorkerOrder()
    {
        var service = new StubPrimeService(IsPrime);

        var result = new PrimesAmount(20).CountPrimesAmount(service).ToArray();

        Assert.That(result, Has.Length.EqualTo(8));
        Assert.That(result, Is.EquivalentTo(new[] { 2, 3, 5, 7, 11, 13, 17, 19 }));
        Assert.That(service.ClearCount, Is.EqualTo(1));
    }

    [Test]
    public async Task CountPrimesAmount_DoesNotReturnBeforeBlockedCheckCompletes()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var service = new StubPrimeService(value =>
        {
            if (value == 2)
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Test did not release the worker.");
            }
            return IsPrime(value);
        });

        var work = Task.Run(() => new PrimesAmount(10).CountPrimesAmount(service));
        try
        {
            Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(work.IsCompleted, Is.False);
        }
        finally
        {
            release.Set();
        }

        var completed = await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.That(completed, Is.SameAs(work));
        var result = (await work).ToArray();
        Assert.That(result, Has.Length.EqualTo(4));
        Assert.That(result, Is.EquivalentTo(new[] { 2, 3, 5, 7 }));
        Assert.That(service.ClearCount, Is.EqualTo(1));
    }

    [Test]
    public void CountPrimesAmount_CheckThrows_ClearsPartialResultsAndCache()
    {
        var service = new StubPrimeService(value =>
            value == 3 ? throw new InvalidOperationException("boom") : IsPrime(value));

        var result = new PrimesAmount(10).CountPrimesAmount(service);

        Assert.That(result, Is.Empty);
        Assert.That(service.ClearCount, Is.EqualTo(1));
    }
}
