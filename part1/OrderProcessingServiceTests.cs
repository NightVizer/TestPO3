using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace Shop.Orders.Tests;

[TestFixture]
public class OrderProcessingServiceTests
{
    // 2026-10-14 is a Wednesday, 16 = Friday, 17 = Saturday, 18 = Sunday
    private static DateTimeOffset At(int day, int hour, int minute = 0) =>
        new(2026, 10, day, hour, minute, 0, TimeSpan.Zero);

    private Mock<IOrderRepository> _orders = null!;
    private Mock<IInventoryService> _inventory = null!;
    private Mock<IPaymentGateway> _payments = null!;
    private Mock<IDiscountEngine> _discounts = null!;
    private Mock<ITaxCalculator> _tax = null!;
    private Mock<INotificationService> _notifications = null!;
    private Mock<IUnitOfWork> _uow = null!;
    private Mock<IClock> _clock = null!;
    private Mock<IDelayer> _delayer = null!;
    private Mock<ILogger<OrderProcessingService>> _logger = null!;

    private Customer _customer = null!;
    private DateTimeOffset _now;
    private List<TimeSpan> _delays = null!;

    [SetUp]
    public void SetUp()
    {
        _orders = new Mock<IOrderRepository>();
        _inventory = new Mock<IInventoryService>();
        _payments = new Mock<IPaymentGateway>();
        _discounts = new Mock<IDiscountEngine>();
        _tax = new Mock<ITaxCalculator>();
        _notifications = new Mock<INotificationService>();
        _uow = new Mock<IUnitOfWork>();
        _clock = new Mock<IClock>();
        _delayer = new Mock<IDelayer>();
        _logger = new Mock<ILogger<OrderProcessingService>>();

        _customer = new Customer(Guid.NewGuid(), "jane@example.com", CustomerTier.Standard, "FI");
        _now = At(14, 12); // Wednesday noon: no happy hour
        _delays = new List<TimeSpan>();

        _orders.Setup(r => r.GetByIdempotencyKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync((Order?)null);
        _orders.Setup(r => r.SaveAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()))
               .Returns(Task.CompletedTask);

        _inventory.Setup(i => i.TryReserveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(true);
        _inventory.Setup(i => i.ReleaseAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .Returns(Task.CompletedTask);

        _payments.Setup(p => p.ChargeAsync(It.IsAny<PaymentRequest>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(Ok());
        _payments.Setup(p => p.RefundAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .Returns(Task.CompletedTask);

        SetupDiscounts();

        // Flat 10% tax on whatever amount is passed in
        _tax.Setup(t => t.Calculate(It.IsAny<string>(), It.IsAny<decimal>()))
            .Returns<string, decimal>((_, amount) => Math.Round(amount * 0.10m, 2));

        _notifications.Setup(n => n.SendConfirmationAsync(It.IsAny<Order>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                      .Returns(Task.CompletedTask);

        // Unit of work simply runs the delegate
        _uow.Setup(u => u.ExecuteAsync(It.IsAny<Func<Task<bool>>>(), It.IsAny<CancellationToken>()))
            .Returns<Func<Task<bool>>, CancellationToken>((work, _) => work());

        _clock.SetupGet(c => c.UtcNow).Returns(() => _now);

        _delayer.Setup(d => d.DelayAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .Callback<TimeSpan, CancellationToken>((d, _) => _delays.Add(d))
                .Returns(Task.CompletedTask);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private OrderProcessingService CreateSut(OrderOptions? options = null) => new(
        _orders.Object, _inventory.Object, _payments.Object, _discounts.Object, _tax.Object,
        _notifications.Object, _uow.Object, _clock.Object, _delayer.Object,
        options ?? new OrderOptions(), _logger.Object);

    private static IReadOnlyList<OrderLine> Lines(params OrderLine[] lines) => lines;

    private static IReadOnlyList<OrderLine> DefaultLines() => Lines(new OrderLine("SKU-1", 2, 50m)); // subtotal 100
    private static IReadOnlyList<OrderLine> SingleHundred() => Lines(new OrderLine("SKU-1", 1, 100m));

    private PlaceOrderRequest Request(IReadOnlyList<OrderLine>? lines = null, string key = "key-1") =>
        new(key, _customer, lines ?? DefaultLines(), "tok");

    private static PaymentResult Ok(string tx = "tx-1") => new(true, false, tx, null);
    private static PaymentResult Transient() => new(false, true, null, "timeout");
    private static PaymentResult Declined(string? error = "Insufficient funds") => new(false, false, null, error);

    private void SetupDiscounts(params Discount[] discounts) =>
        _discounts.Setup(d => d.GetApplicableAsync(
                        It.IsAny<Customer>(), It.IsAny<IReadOnlyList<OrderLine>>(),
                        It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync((IReadOnlyList<Discount>)discounts);

    private void SetupPayments(params PaymentResult[] results)
    {
        var sequence = _payments.SetupSequence(p => p.ChargeAsync(It.IsAny<PaymentRequest>(), It.IsAny<CancellationToken>()));
        foreach (var r in results) sequence = sequence.ReturnsAsync(r);
    }

    private void VerifyLogged(LogLevel level, Times times) =>
        _logger.Verify(l => l.Log(
            level, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), It.IsAny<Exception?>(),
            (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()), times);

    private void VerifyNothingReserved() =>
        _inventory.Verify(i => i.TryReserveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never());

    private void VerifyNothingReleased() =>
        _inventory.Verify(i => i.ReleaseAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never());

    private void VerifyNothingCharged() =>
        _payments.Verify(p => p.ChargeAsync(It.IsAny<PaymentRequest>(), It.IsAny<CancellationToken>()), Times.Never());

    private void VerifyNothingSaved() =>
        _orders.Verify(r => r.SaveAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()), Times.Never());

    private void VerifyNothingSent() =>
        _notifications.Verify(n => n.SendConfirmationAsync(It.IsAny<Order>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never());

    private static void AssertFailure(OrderResult result, FailureReason reason)
    {
        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.Failure, Is.EqualTo(reason));
            Assert.That(result.Order, Is.Null);
        });
    }

    // ------------------------------------------------------------------
    // 1. Validation
    // ------------------------------------------------------------------

    [Test]
    public void PlaceOrderAsync_NullRequest_ThrowsArgumentNullException()
    {
        var sut = CreateSut();

        Assert.ThrowsAsync<ArgumentNullException>(() => sut.PlaceOrderAsync(null!));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public async Task PlaceOrderAsync_MissingIdempotencyKey_ReturnsValidationFailure(string? key)
    {
        var result = await CreateSut().PlaceOrderAsync(Request(key: key!));

        AssertFailure(result, FailureReason.Validation);
        Assert.That(result.Message, Does.Contain("Idempotency key"));
        _orders.Verify(r => r.GetByIdempotencyKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Test]
    public async Task PlaceOrderAsync_NullLines_ReturnsValidationFailure()
    {
        var request = new PlaceOrderRequest("key-1", _customer, null!, "tok");

        var result = await CreateSut().PlaceOrderAsync(request);

        AssertFailure(result, FailureReason.Validation);
        Assert.That(result.Message, Does.Contain("at least one line"));
    }

    [Test]
    public async Task PlaceOrderAsync_EmptyLines_ReturnsValidationFailure()
    {
        var result = await CreateSut().PlaceOrderAsync(Request(Lines()));

        AssertFailure(result, FailureReason.Validation);
        Assert.That(result.Message, Does.Contain("at least one line"));
    }

    [Test]
    public async Task PlaceOrderAsync_MoreThanMaxLines_ReturnsValidationFailure()
    {
        var lines = Enumerable.Range(1, 51).Select(i => new OrderLine($"SKU-{i}", 1, 1m)).ToArray();

        var result = await CreateSut().PlaceOrderAsync(Request(lines));

        AssertFailure(result, FailureReason.Validation);
        Assert.That(result.Message, Does.Contain("50"));
        VerifyNothingReserved();
    }

    [Test]
    public async Task PlaceOrderAsync_ExactlyMaxLines_IsAccepted()
    {
        var lines = Enumerable.Range(1, 50).Select(i => new OrderLine($"SKU-{i}", 1, 1m)).ToArray();

        var result = await CreateSut().PlaceOrderAsync(Request(lines));

        Assert.That(result.IsSuccess, Is.True);
        _inventory.Verify(i => i.TryReserveAsync(It.IsAny<string>(), 1, It.IsAny<CancellationToken>()), Times.Exactly(50));
    }

    [Test]
    public async Task PlaceOrderAsync_CustomMaxLines_IsRespected()
    {
        var options = new OrderOptions { MaxLines = 1 };
        var lines = Lines(new OrderLine("A", 1, 1m), new OrderLine("B", 1, 1m));

        var result = await CreateSut(options).PlaceOrderAsync(Request(lines));

        AssertFailure(result, FailureReason.Validation);
    }

    // A valid line comes first so the test proves *any* invalid line is rejected
    [TestCase(0, 10)]
    [TestCase(-1, 10)]
    [TestCase(1, -1)]
    public async Task PlaceOrderAsync_InvalidLine_ReturnsValidationFailure(int quantity, int unitPrice)
    {
        var lines = Lines(new OrderLine("GOOD", 1, 10m), new OrderLine("BAD", quantity, unitPrice));

        var result = await CreateSut().PlaceOrderAsync(Request(lines));

        AssertFailure(result, FailureReason.Validation);
        Assert.That(result.Message, Does.Contain("Invalid quantity or price"));
        VerifyNothingReserved();
        VerifyNothingCharged();
    }

    [Test]
    public async Task PlaceOrderAsync_ZeroUnitPrice_IsValid()
    {
        var result = await CreateSut().PlaceOrderAsync(Request(Lines(new OrderLine("FREE", 1, 0m))));

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Order!.Total, Is.EqualTo(0m));
        _payments.Verify(p => p.ChargeAsync(It.Is<PaymentRequest>(r => r.Amount == 0m), It.IsAny<CancellationToken>()), Times.Once());
    }

    // ------------------------------------------------------------------
    // 2. Idempotency
    // ------------------------------------------------------------------

    [Test]
    public async Task PlaceOrderAsync_DuplicateKey_ReturnsExistingOrderWithoutSideEffects()
    {
        var existing = new Order { IdempotencyKey = "key-1", Status = OrderStatus.Confirmed };
        _orders.Setup(r => r.GetByIdempotencyKeyAsync("key-1", It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var result = await CreateSut().PlaceOrderAsync(Request());

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.True);
            Assert.That(result.Order, Is.SameAs(existing));
            Assert.That(result.Message, Is.EqualTo("Duplicate request."));
        });
        VerifyNothingReserved();
        VerifyNothingCharged();
        VerifyNothingSaved();
        VerifyNothingSent();
        _discounts.VerifyNoOtherCalls();
        VerifyLogged(LogLevel.Information, Times.Once());
    }

    // ------------------------------------------------------------------
    // 3. Discount calculation
    // ------------------------------------------------------------------

    private static IEnumerable<TestCaseData> DiscountCases()
    {
        yield return new TestCaseData(CustomerTier.Standard, Array.Empty<Discount>(), 0m)
            .SetName("No discounts -> 0%");
        yield return new TestCaseData(CustomerTier.Standard,
                new[] { new Discount("A", 10, false), new Discount("B", 20, false) }, 20m)
            .SetName("Non-stackable discounts -> highest wins");
        yield return new TestCaseData(CustomerTier.Standard,
                new[] { new Discount("A", 5, true), new Discount("B", 7, true) }, 12m)
            .SetName("Stackable discounts -> summed");
        yield return new TestCaseData(CustomerTier.Standard,
                new[] { new Discount("A", 15, false), new Discount("B", 5, true), new Discount("C", 5, true) }, 15m)
            .SetName("Non-stackable (15) beats stackable sum (10)");
        yield return new TestCaseData(CustomerTier.Standard,
                new[] { new Discount("A", 5, false), new Discount("B", 10, true), new Discount("C", 5, true) }, 15m)
            .SetName("Stackable sum (15) beats non-stackable (5)");
        yield return new TestCaseData(CustomerTier.Gold,
                new[] { new Discount("A", 10, false) }, 10m)
            .SetName("Gold tier gets no extra bonus");
        yield return new TestCaseData(CustomerTier.Platinum, Array.Empty<Discount>(), 5m)
            .SetName("Platinum with no discounts -> 5% bonus");
        yield return new TestCaseData(CustomerTier.Platinum,
                new[] { new Discount("A", 10, false) }, 15m)
            .SetName("Platinum bonus is added on top of discount");
        yield return new TestCaseData(CustomerTier.Standard,
                new[] { new Discount("A", 60, false) }, 50m)
            .SetName("Discount capped at 50%");
        yield return new TestCaseData(CustomerTier.Platinum,
                new[] { new Discount("A", 48, false) }, 50m)
            .SetName("Discount + Platinum bonus capped at 50%");
    }

    [TestCaseSource(nameof(DiscountCases))]
    public async Task PlaceOrderAsync_AppliesExpectedDiscountPercent(CustomerTier tier, Discount[] discounts, decimal expectedPercent)
    {
        _customer = _customer with { Tier = tier };
        SetupDiscounts(discounts);

        var result = await CreateSut().PlaceOrderAsync(Request(SingleHundred()));

        var taxable = 100m - expectedPercent;
        var tax = Math.Round(taxable * 0.10m, 2);
        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.True);
            Assert.That(result.Order!.Subtotal, Is.EqualTo(100m));
            Assert.That(result.Order.DiscountTotal, Is.EqualTo(expectedPercent));
            Assert.That(result.Order.Tax, Is.EqualTo(tax));
            Assert.That(result.Order.Total, Is.EqualTo(taxable + tax));
        });
    }

    private static IEnumerable<TestCaseData> HappyHourCases()
    {
        yield return new TestCaseData(At(17, 16, 59), 0m).SetName("Saturday 16:59 -> no bonus");
        yield return new TestCaseData(At(17, 17), 2m).SetName("Saturday 17:00 -> bonus");
        yield return new TestCaseData(At(17, 19, 59), 2m).SetName("Saturday 19:59 -> bonus");
        yield return new TestCaseData(At(17, 20), 0m).SetName("Saturday 20:00 -> no bonus");
        yield return new TestCaseData(At(18, 18), 2m).SetName("Sunday 18:00 -> bonus");
        yield return new TestCaseData(At(18, 16, 59), 0m).SetName("Sunday 16:59 -> no bonus");
        yield return new TestCaseData(At(16, 18), 0m).SetName("Friday 18:00 -> no bonus");
    }

    [TestCaseSource(nameof(HappyHourCases))]
    public async Task PlaceOrderAsync_WeekendHappyHour_AddsTwoPercentOnlyInWindow(DateTimeOffset now, decimal expectedPercent)
    {
        _now = now;

        var result = await CreateSut().PlaceOrderAsync(Request(SingleHundred()));

        Assert.That(result.Order!.DiscountTotal, Is.EqualTo(expectedPercent));
    }

    [Test]
    public async Task PlaceOrderAsync_PlatinumDuringHappyHour_StacksBothBonuses()
    {
        _customer = _customer with { Tier = CustomerTier.Platinum };
        _now = At(17, 18);

        var result = await CreateSut().PlaceOrderAsync(Request(SingleHundred()));

        Assert.That(result.Order!.DiscountTotal, Is.EqualTo(7m));
    }

    [Test]
    public async Task PlaceOrderAsync_CustomPlatinumBonusAndCap_AreRespected()
    {
        _customer = _customer with { Tier = CustomerTier.Platinum };
        SetupDiscounts(new Discount("A", 10, false));
        var options = new OrderOptions { PlatinumExtraPercent = 20m, MaxDiscountPercent = 25m };

        var result = await CreateSut(options).PlaceOrderAsync(Request(SingleHundred()));

        Assert.That(result.Order!.DiscountTotal, Is.EqualTo(25m));
    }

    [Test]
    public async Task PlaceOrderAsync_DiscountRoundsMidpointAwayFromZero()
    {
        // 9.80 * 2.5% = 0.245 -> 0.25 (banker's rounding would give 0.24)
        SetupDiscounts(new Discount("A", 2.5m, false));

        var result = await CreateSut().PlaceOrderAsync(Request(Lines(new OrderLine("SKU-1", 1, 9.80m))));

        Assert.That(result.Order!.DiscountTotal, Is.EqualTo(0.25m));
    }

    [Test]
    public async Task PlaceOrderAsync_TaxIsCalculatedOnDiscountedAmountForCustomerCountry()
    {
        SetupDiscounts(new Discount("A", 10, false));

        await CreateSut().PlaceOrderAsync(Request(SingleHundred()));

        _tax.Verify(t => t.Calculate("FI", 90m), Times.Once());
    }

    [Test]
    public async Task PlaceOrderAsync_PassesCustomerLinesAndClockTimeToDiscountEngine()
    {
        var request = Request();

        await CreateSut().PlaceOrderAsync(request);

        _discounts.Verify(d => d.GetApplicableAsync(_customer, request.Lines, _now, It.IsAny<CancellationToken>()), Times.Once());
    }

    // ------------------------------------------------------------------
    // 4. Manual review threshold
    // ------------------------------------------------------------------

    [Test]
    public async Task PlaceOrderAsync_HighValueOrder_GoesToManualReviewWithoutReservingOrCharging()
    {
        var result = await CreateSut().PlaceOrderAsync(Request(Lines(new OrderLine("BIG", 1, 10_000m))));

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.True);
            Assert.That(result.Message, Is.EqualTo("Order requires manual review."));
            Assert.That(result.Order!.Status, Is.EqualTo(OrderStatus.PendingReview));
            Assert.That(result.Order.Total, Is.EqualTo(11_000m));
            Assert.That(result.Order.TransactionId, Is.Null);
        });
        _orders.Verify(r => r.SaveAsync(It.Is<Order>(o => o.Status == OrderStatus.PendingReview), It.IsAny<CancellationToken>()), Times.Once());
        VerifyNothingReserved();
        VerifyNothingCharged();
        VerifyNothingSent();
    }

    [Test]
    public async Task PlaceOrderAsync_TotalExactlyAtThreshold_GoesToManualReview()
    {
        // Default order total is 110.00
        var result = await CreateSut(new OrderOptions { ManualReviewThreshold = 110m })
            .PlaceOrderAsync(Request());

        Assert.That(result.Order!.Status, Is.EqualTo(OrderStatus.PendingReview));
        VerifyNothingCharged();
    }

    [Test]
    public async Task PlaceOrderAsync_TotalJustBelowThreshold_IsProcessedNormally()
    {
        var result = await CreateSut(new OrderOptions { ManualReviewThreshold = 110.01m })
            .PlaceOrderAsync(Request());

        Assert.That(result.Order!.Status, Is.EqualTo(OrderStatus.Confirmed));
        _payments.Verify(p => p.ChargeAsync(It.IsAny<PaymentRequest>(), It.IsAny<CancellationToken>()), Times.Once());
    }

    // ------------------------------------------------------------------
    // 5. Inventory reservation
    // ------------------------------------------------------------------

    [Test]
    public async Task PlaceOrderAsync_FirstLineOutOfStock_FailsWithoutReleasingAnything()
    {
        _inventory.Setup(i => i.TryReserveAsync("SKU-1", It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await CreateSut().PlaceOrderAsync(Request());

        AssertFailure(result, FailureReason.OutOfStock);
        Assert.That(result.Message, Does.Contain("SKU-1"));
        VerifyNothingReleased();
        VerifyNothingCharged();
        VerifyNothingSaved();
    }

    [Test]
    public async Task PlaceOrderAsync_LaterLineOutOfStock_ReleasesOnlyAlreadyReservedLinesAndStops()
    {
        var lines = Lines(
            new OrderLine("A", 1, 10m), new OrderLine("B", 2, 10m),
            new OrderLine("C", 3, 10m), new OrderLine("D", 4, 10m));
        _inventory.Setup(i => i.TryReserveAsync("C", 3, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await CreateSut().PlaceOrderAsync(Request(lines));

        AssertFailure(result, FailureReason.OutOfStock);
        Assert.That(result.Message, Does.Contain("C"));
        _inventory.Verify(i => i.ReleaseAsync("A", 1, CancellationToken.None), Times.Once());
        _inventory.Verify(i => i.ReleaseAsync("B", 2, CancellationToken.None), Times.Once());
        _inventory.Verify(i => i.ReleaseAsync("C", It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never());
        _inventory.Verify(i => i.TryReserveAsync("D", It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never());
        VerifyNothingCharged();
    }

    [Test]
    public async Task PlaceOrderAsync_ReleaseThrowsDuringRollback_StillReleasesRemainingLinesAndReportsOutOfStock()
    {
        var lines = Lines(new OrderLine("A", 1, 10m), new OrderLine("B", 1, 10m), new OrderLine("C", 1, 10m));
        _inventory.Setup(i => i.TryReserveAsync("C", 1, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _inventory.Setup(i => i.ReleaseAsync("A", It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .ThrowsAsync(new InvalidOperationException("inventory down"));

        var result = await CreateSut().PlaceOrderAsync(Request(lines));

        AssertFailure(result, FailureReason.OutOfStock);
        _inventory.Verify(i => i.ReleaseAsync("B", 1, CancellationToken.None), Times.Once());
        VerifyLogged(LogLevel.Error, Times.Once());
    }

    [Test]
    public async Task PlaceOrderAsync_WaitsForReservationBeforeTryingNextLine()
    {
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lines = Lines(new OrderLine("A", 1, 10m), new OrderLine("B", 2, 10m));
        _inventory.Setup(i => i.TryReserveAsync("A", 1, It.IsAny<CancellationToken>()))
                  .Returns(pending.Task);
        _inventory.Setup(i => i.TryReserveAsync("B", 2, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(false);

        var work = CreateSut().PlaceOrderAsync(Request(lines));
        Assert.That(work.IsCompleted, Is.False);
        _inventory.Verify(i => i.TryReserveAsync("B", 2, It.IsAny<CancellationToken>()), Times.Never());

        pending.SetResult(true);
        var result = await work;

        AssertFailure(result, FailureReason.OutOfStock);
        _inventory.Verify(i => i.TryReserveAsync("B", 2, It.IsAny<CancellationToken>()), Times.Once());
        _inventory.Verify(i => i.ReleaseAsync("A", 1, CancellationToken.None), Times.Once());
        VerifyNothingCharged();
    }

    [Test]
    public void PlaceOrderAsync_ReservationThrowsAfterPartialSuccess_ReleasesEarlierLine()
    {
        var lines = Lines(new OrderLine("A", 1, 10m), new OrderLine("B", 2, 10m));
        _inventory.Setup(i => i.TryReserveAsync("B", 2, It.IsAny<CancellationToken>()))
                  .ThrowsAsync(new InvalidOperationException("inventory unavailable"));

        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await CreateSut().PlaceOrderAsync(Request(lines)));

        _inventory.Verify(i => i.ReleaseAsync("A", 1, CancellationToken.None), Times.Once());
        VerifyNothingCharged();
    }

    // ------------------------------------------------------------------
    // 6. Payment: success, retries, declines
    // ------------------------------------------------------------------

    [Test]
    public async Task PlaceOrderAsync_PaymentSucceedsFirstTry_ChargesOnceWithoutDelay()
    {
        var request = Request();

        var result = await CreateSut().PlaceOrderAsync(request);

        Assert.That(result.IsSuccess, Is.True);
        _payments.Verify(p => p.ChargeAsync(
            It.Is<PaymentRequest>(r =>
                r.Amount == 110m && r.Token == "tok" &&
                r.IdempotencyKey == "key-1" && r.CustomerId == _customer.Id),
            It.IsAny<CancellationToken>()), Times.Once());
        Assert.That(_delays, Is.Empty);
    }

    [Test]
    public async Task PlaceOrderAsync_TransientFailureThenSuccess_RetriesOnceAfter200ms()
    {
        SetupPayments(Transient(), Ok("tx-2"));

        var result = await CreateSut().PlaceOrderAsync(Request());

        Assert.Multiple(() =>
        {
            Assert.That(result.Order!.Status, Is.EqualTo(OrderStatus.Confirmed));
            Assert.That(result.Order.TransactionId, Is.EqualTo("tx-2"));
            Assert.That(_delays, Is.EqualTo(new[] { TimeSpan.FromMilliseconds(200) }));
        });
        _payments.Verify(p => p.ChargeAsync(It.IsAny<PaymentRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Test]
    public async Task PlaceOrderAsync_TwoTransientFailuresThenSuccess_UsesLinearBackoff()
    {
        SetupPayments(Transient(), Transient(), Ok());

        var result = await CreateSut().PlaceOrderAsync(Request());

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(_delays, Is.EqualTo(new[] { TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(400) }));
        _payments.Verify(p => p.ChargeAsync(It.IsAny<PaymentRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Test]
    public async Task PlaceOrderAsync_TransientFailuresExhausted_ReturnsPaymentUnavailableAndReleasesInventory()
    {
        SetupPayments(Transient(), Transient(), Transient());

        var result = await CreateSut().PlaceOrderAsync(Request());

        AssertFailure(result, FailureReason.PaymentUnavailable);
        Assert.That(result.Message, Is.EqualTo("Payment provider unavailable."));
        _payments.Verify(p => p.ChargeAsync(It.IsAny<PaymentRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
        Assert.That(_delays, Has.Count.EqualTo(2), "no delay after the final attempt");
        _inventory.Verify(i => i.ReleaseAsync("SKU-1", 2, CancellationToken.None), Times.Once());
        VerifyNothingSaved();
        VerifyNothingSent();
    }

    [Test]
    public async Task PlaceOrderAsync_SingleAttemptConfigured_DoesNotRetryOrDelay()
    {
        SetupPayments(Transient());

        var result = await CreateSut(new OrderOptions { MaxPaymentAttempts = 1 }).PlaceOrderAsync(Request());

        AssertFailure(result, FailureReason.PaymentUnavailable);
        _payments.Verify(p => p.ChargeAsync(It.IsAny<PaymentRequest>(), It.IsAny<CancellationToken>()), Times.Once());
        Assert.That(_delays, Is.Empty);
    }

    [Test]
    public async Task PlaceOrderAsync_PaymentDeclined_ReturnsGatewayErrorWithoutRetry()
    {
        SetupPayments(Declined("Insufficient funds"));

        var result = await CreateSut().PlaceOrderAsync(Request());

        AssertFailure(result, FailureReason.PaymentDeclined);
        Assert.That(result.Message, Is.EqualTo("Insufficient funds"));
        _payments.Verify(p => p.ChargeAsync(It.IsAny<PaymentRequest>(), It.IsAny<CancellationToken>()), Times.Once());
        Assert.That(_delays, Is.Empty);
        _inventory.Verify(i => i.ReleaseAsync("SKU-1", 2, CancellationToken.None), Times.Once());
        VerifyNothingSaved();
        VerifyNothingSent();
    }

    [Test]
    public async Task PlaceOrderAsync_PaymentDeclinedWithoutError_UsesDefaultMessage()
    {
        SetupPayments(Declined(null));

        var result = await CreateSut().PlaceOrderAsync(Request());

        AssertFailure(result, FailureReason.PaymentDeclined);
        Assert.That(result.Message, Is.EqualTo("Payment declined."));
    }

    [Test]
    public async Task PlaceOrderAsync_TransientFailureThenDecline_StopsRetryingAndReportsDecline()
    {
        SetupPayments(Transient(), Declined("Card expired"));

        var result = await CreateSut().PlaceOrderAsync(Request());

        AssertFailure(result, FailureReason.PaymentDeclined);
        Assert.That(result.Message, Is.EqualTo("Card expired"));
        _payments.Verify(p => p.ChargeAsync(It.IsAny<PaymentRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        Assert.That(_delays, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task PlaceOrderAsync_ZeroPaymentAttemptsConfigured_NeverCharges_AndFailsAsDeclined()
    {
        // Covers the `payment is null` branch (loop body never runs)
        var result = await CreateSut(new OrderOptions { MaxPaymentAttempts = 0 }).PlaceOrderAsync(Request());

        AssertFailure(result, FailureReason.PaymentDeclined);
        Assert.That(result.Message, Is.EqualTo("Payment declined."));
        VerifyNothingCharged();
        _inventory.Verify(i => i.ReleaseAsync("SKU-1", 2, CancellationToken.None), Times.Once());
    }

    // ------------------------------------------------------------------
    // 7. Persistence and compensation
    // ------------------------------------------------------------------

    [Test]
    public async Task PlaceOrderAsync_HappyPath_ReturnsConfirmedOrderWithExpectedTotals()
    {
        var result = await CreateSut().PlaceOrderAsync(Request());

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.True);
            Assert.That(result.Message, Is.Null);
            Assert.That(result.Order!.Status, Is.EqualTo(OrderStatus.Confirmed));
            Assert.That(result.Order.IdempotencyKey, Is.EqualTo("key-1"));
            Assert.That(result.Order.CustomerId, Is.EqualTo(_customer.Id));
            Assert.That(result.Order.Subtotal, Is.EqualTo(100m));
            Assert.That(result.Order.DiscountTotal, Is.EqualTo(0m));
            Assert.That(result.Order.Tax, Is.EqualTo(10m));
            Assert.That(result.Order.Total, Is.EqualTo(110m));
            Assert.That(result.Order.TransactionId, Is.EqualTo("tx-1"));
            Assert.That(result.Order.CreatedAt, Is.EqualTo(_now));
        });
        _orders.Verify(r => r.SaveAsync(
            It.Is<Order>(o => o.Status == OrderStatus.Confirmed && o.TransactionId == "tx-1"),
            It.IsAny<CancellationToken>()), Times.Once());
        _payments.Verify(p => p.RefundAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never());
        VerifyNothingReleased();
    }

    [Test]
    public async Task PlaceOrderAsync_HappyPath_ExecutesStepsInExpectedOrder()
    {
        var events = new List<string>();
        _inventory.Setup(i => i.TryReserveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .Callback<string, int, CancellationToken>((sku, _, _) => events.Add($"reserve:{sku}"))
                  .ReturnsAsync(true);
        _payments.Setup(p => p.ChargeAsync(It.IsAny<PaymentRequest>(), It.IsAny<CancellationToken>()))
                 .Callback<PaymentRequest, CancellationToken>((_, _) => events.Add("charge"))
                 .ReturnsAsync(Ok());
        _orders.Setup(r => r.SaveAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()))
               .Callback<Order, CancellationToken>((_, _) => events.Add("save"))
               .Returns(Task.CompletedTask);
        _notifications.Setup(n => n.SendConfirmationAsync(It.IsAny<Order>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                      .Callback<Order, string, CancellationToken>((_, _, _) => events.Add("notify"))
                      .Returns(Task.CompletedTask);

        await CreateSut().PlaceOrderAsync(Request(Lines(new OrderLine("A", 1, 10m), new OrderLine("B", 1, 10m))));

        Assert.That(events, Is.EqualTo(new[] { "reserve:A", "reserve:B", "charge", "save", "notify" }));
    }

    [Test]
    public void PlaceOrderAsync_SaveFails_RefundsReleasesInventoryAndRethrows()
    {
        _orders.Setup(r => r.SaveAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()))
               .ThrowsAsync(new InvalidOperationException("db down"));
        var lines = Lines(new OrderLine("A", 1, 10m), new OrderLine("B", 2, 10m));

        var ex = Assert.ThrowsAsync<InvalidOperationException>(() => CreateSut().PlaceOrderAsync(Request(lines)));

        Assert.That(ex!.Message, Is.EqualTo("db down"));
        _payments.Verify(p => p.RefundAsync("tx-1", CancellationToken.None), Times.Once());
        _inventory.Verify(i => i.ReleaseAsync("A", 1, CancellationToken.None), Times.Once());
        _inventory.Verify(i => i.ReleaseAsync("B", 2, CancellationToken.None), Times.Once());
        VerifyNothingSent();
        VerifyLogged(LogLevel.Error, Times.Once());
    }

    [Test]
    public void PlaceOrderAsync_SaveFails_CompensationIgnoresCallerCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        _orders.Setup(r => r.SaveAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()))
               .ThrowsAsync(new InvalidOperationException("db down"));

        Assert.ThrowsAsync<InvalidOperationException>(() => CreateSut().PlaceOrderAsync(Request(), cts.Token));

        _payments.Verify(p => p.RefundAsync("tx-1", CancellationToken.None), Times.Once());
        _payments.Verify(p => p.RefundAsync(It.IsAny<string>(), cts.Token), Times.Never());
        _inventory.Verify(i => i.ReleaseAsync("SKU-1", 2, CancellationToken.None), Times.Once());
    }

    [Test]
    public void PlaceOrderAsync_SaveFailsAndOneReleaseFails_StillRefundsReleasesOthersAndRethrowsOriginal()
    {
        _orders.Setup(r => r.SaveAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()))
               .ThrowsAsync(new InvalidOperationException("db down"));
        _inventory.Setup(i => i.ReleaseAsync("A", It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .ThrowsAsync(new TimeoutException("inventory timeout"));
        var lines = Lines(new OrderLine("A", 1, 10m), new OrderLine("B", 1, 10m));

        var ex = Assert.ThrowsAsync<InvalidOperationException>(() => CreateSut().PlaceOrderAsync(Request(lines)));

        Assert.That(ex!.Message, Is.EqualTo("db down"));
        _payments.Verify(p => p.RefundAsync("tx-1", CancellationToken.None), Times.Once());
        _inventory.Verify(i => i.ReleaseAsync("B", 1, CancellationToken.None), Times.Once());
        VerifyLogged(LogLevel.Error, Times.Exactly(2)); // save failure + release failure
    }

    [Test]
    public void PlaceOrderAsync_SaveCancelled_AlsoCompensatesAndRethrows()
    {
        _orders.Setup(r => r.SaveAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()))
               .ThrowsAsync(new OperationCanceledException());

        Assert.ThrowsAsync<OperationCanceledException>(() => CreateSut().PlaceOrderAsync(Request()));

        _payments.Verify(p => p.RefundAsync("tx-1", CancellationToken.None), Times.Once());
        _inventory.Verify(i => i.ReleaseAsync("SKU-1", 2, CancellationToken.None), Times.Once());
    }

    // Characterization test: documents CURRENT behaviour, which is arguably a gap.
    // If the refund throws, its exception replaces the original and inventory is never released.
    [Test]
    public void PlaceOrderAsync_SaveFailsAndRefundFails_PropagatesRefundExceptionAndSkipsInventoryRelease()
    {
        _orders.Setup(r => r.SaveAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()))
               .ThrowsAsync(new InvalidOperationException("db down"));
        _payments.Setup(p => p.RefundAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .ThrowsAsync(new TimeoutException("gateway timeout"));

        var ex = Assert.ThrowsAsync<TimeoutException>(() => CreateSut().PlaceOrderAsync(Request()));

        Assert.That(ex!.Message, Is.EqualTo("gateway timeout"));
        VerifyNothingReleased();
    }

    // ------------------------------------------------------------------
    // 8. Notification
    // ------------------------------------------------------------------

    [Test]
    public async Task PlaceOrderAsync_Success_SendsConfirmationToCustomerEmail()
    {
        var result = await CreateSut().PlaceOrderAsync(Request());

        _notifications.Verify(n => n.SendConfirmationAsync(result.Order!, "jane@example.com", It.IsAny<CancellationToken>()), Times.Once());
    }

    [Test]
    public async Task PlaceOrderAsync_NotificationFails_OrderStillSucceedsAndWarningIsLogged()
    {
        _notifications.Setup(n => n.SendConfirmationAsync(It.IsAny<Order>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                      .ThrowsAsync(new InvalidOperationException("smtp down"));

        var result = await CreateSut().PlaceOrderAsync(Request());

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.True);
            Assert.That(result.Order!.Status, Is.EqualTo(OrderStatus.Confirmed));
        });
        VerifyLogged(LogLevel.Warning, Times.Once());
        _payments.Verify(p => p.RefundAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never());
        VerifyNothingReleased();
    }

    [Test]
    public void PlaceOrderAsync_NotificationCancelled_PropagatesOperationCanceledException()
    {
        _notifications.Setup(n => n.SendConfirmationAsync(It.IsAny<Order>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                      .ThrowsAsync(new OperationCanceledException());

        Assert.ThrowsAsync<OperationCanceledException>(() => CreateSut().PlaceOrderAsync(Request()));

        // The order was already persisted and must not be compensated
        _orders.Verify(r => r.SaveAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()), Times.Once());
        _payments.Verify(p => p.RefundAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never());
        VerifyLogged(LogLevel.Warning, Times.Never());
    }

    // ------------------------------------------------------------------
    // 9. Cancellation token propagation
    // ------------------------------------------------------------------

    [Test]
    public async Task PlaceOrderAsync_ForwardsCancellationTokenToDependencies()
    {
        using var cts = new CancellationTokenSource();
        var token = cts.Token;
        SetupPayments(Transient(), Ok()); // forces a delay call too
        var request = Request();

        await CreateSut().PlaceOrderAsync(request, token);

        _orders.Verify(r => r.GetByIdempotencyKeyAsync("key-1", token), Times.Once());
        _discounts.Verify(d => d.GetApplicableAsync(_customer, request.Lines, _now, token), Times.Once());
        _inventory.Verify(i => i.TryReserveAsync("SKU-1", 2, token), Times.Once());
        _payments.Verify(p => p.ChargeAsync(It.IsAny<PaymentRequest>(), token), Times.Exactly(2));
        _delayer.Verify(d => d.DelayAsync(It.IsAny<TimeSpan>(), token), Times.Once());
        _uow.Verify(u => u.ExecuteAsync(It.IsAny<Func<Task<bool>>>(), token), Times.Once());
        _orders.Verify(r => r.SaveAsync(It.IsAny<Order>(), token), Times.Once());
        _notifications.Verify(n => n.SendConfirmationAsync(It.IsAny<Order>(), "jane@example.com", token), Times.Once());
    }
}
