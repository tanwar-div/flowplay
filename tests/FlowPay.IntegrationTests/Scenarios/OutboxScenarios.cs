using System.Net;
using FlowPay.Application.Abstractions;
using FlowPay.Application.Outbox;
using FlowPay.Application.Payments;
using FlowPay.Infrastructure.Messaging;
using FlowPay.Infrastructure.Outbox;
using FlowPay.Infrastructure.Projections;
using FlowPay.IntegrationTests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowPay.IntegrationTests.Scenarios;

/// <summary>
/// The transactional outbox and the consumer-side deduplication that pairs with it.
/// </summary>
[Collection(FlowPayCollection.Name)]
public class OutboxScenarios
{
    private readonly FlowPayFixture _fixture;
    private readonly FlowPayClient _client;

    public OutboxScenarios(FlowPayFixture fixture)
    {
        _fixture = fixture;
        _client = new FlowPayClient(fixture.Api.CreateClient());
    }

    /// <summary>
    /// The outbox row and the payment commit together, so the event exists the moment
    /// the API responds — before any broker has been involved.
    /// </summary>
    [Fact]
    public async Task A_capture_writes_its_event_in_the_same_transaction()
    {
        Guid payer = await _client.CreateAccountAsync("USD", 500_000L);
        Guid payee = await _client.CreateAccountAsync("USD", 0L);

        ApiResponse<PaymentDto> payment = await _client.CreatePaymentAsync(
            payer, payee, 50_000L, NewKey());

        Assert.Equal(HttpStatusCode.Created, payment.Status);

        IReadOnlyList<OutboxMessage> pending = await ReadPendingAsync();

        Assert.Contains(pending, message =>
            message.EventType == EventTypes.PaymentAuthorized
            && message.AggregateId == payment.Body!.Id);

        Assert.Contains(pending, message =>
            message.EventType == EventTypes.PaymentCaptured
            && message.AggregateId == payment.Body!.Id);
    }

    /// <summary>
    /// A rejected payment must leave no event behind: the transaction that would have
    /// carried it rolled back.
    /// </summary>
    [Fact]
    public async Task A_rejected_payment_leaves_no_outbox_row()
    {
        Guid payer = await _client.CreateAccountAsync("USD", 500_000L);
        Guid payee = await _client.CreateAccountAsync("EUR", 0L);

        await DrainOutboxAsync();

        ApiResponse<PaymentDto> rejected = await _client.CreatePaymentAsync(
            payer, payee, 10_000L, NewKey());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.Status);

        IReadOnlyList<OutboxMessage> pending = await ReadPendingAsync();
        Assert.Empty(pending);
    }

    [Fact]
    public async Task The_dispatcher_publishes_pending_rows_and_marks_them_dispatched()
    {
        Guid payer = await _client.CreateAccountAsync("USD", 500_000L);
        Guid payee = await _client.CreateAccountAsync("USD", 0L);

        await _client.CreatePaymentAsync(payer, payee, 25_000L, NewKey());

        OutboxDepth before = await ReadDepthAsync();
        Assert.True(before.Pending > 0L);

        await DrainOutboxAsync();

        OutboxDepth after = await ReadDepthAsync();
        Assert.Equal(0L, after.Pending);
        Assert.Equal(0L, after.Failed);
    }

    /// <summary>
    /// The inbox turns at-least-once delivery into exactly-once effects. The projection
    /// accumulates, so a second application would be visible as a doubled total.
    /// </summary>
    [Fact]
    public async Task A_redelivered_event_is_applied_only_once()
    {
        Guid accountId = await _client.CreateAccountAsync("USD", 0L);
        Guid messageId = Guid.CreateVersion7();
        const string Consumer = "settlement-projection";

        PaymentCapturedEvent captured = new(
            messageId,
            DateTimeOffset.UtcNow,
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            accountId,
            100_000L,
            2_930L,
            97_070L,
            "USD");

        // First delivery: the claim is new, so the projection is applied.
        Assert.True(await ApplyCaptureAsync(messageId, Consumer, captured));

        // Redelivery of the same event id: the claim collides and the work is skipped.
        Assert.False(await ApplyCaptureAsync(messageId, Consumer, captured));
        Assert.False(await ApplyCaptureAsync(messageId, Consumer, captured));

        using IServiceScope scope = _fixture.Api.CreateScope();
        SettlementProjectionStore projections =
            scope.ServiceProvider.GetRequiredService<SettlementProjectionStore>();

        SettlementSummary? summary = await projections.FindAsync(
            accountId, TestContext.Current.CancellationToken);

        Assert.NotNull(summary);
        Assert.Equal(1L, summary.CapturedCount);
        Assert.Equal(100_000L, summary.CapturedGrossMinorUnits);
        Assert.Equal(97_070L, summary.CapturedNetMinorUnits);
    }

    /// <summary>
    /// The inbox is keyed by event and consumer, so two consumers both handle the same
    /// event exactly once each.
    /// </summary>
    [Fact]
    public async Task Two_consumers_each_handle_the_same_event_once()
    {
        Guid accountId = await _client.CreateAccountAsync("USD", 0L);
        Guid messageId = Guid.CreateVersion7();

        PaymentCapturedEvent captured = new(
            messageId,
            DateTimeOffset.UtcNow,
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            accountId,
            10_000L,
            320L,
            9_680L,
            "USD");

        Assert.True(await ApplyCaptureAsync(messageId, "settlement-projection", captured));
        Assert.True(await ApplyCaptureAsync(messageId, "payment-audit", captured));

        // Each consumer's own redelivery is still suppressed.
        Assert.False(await ApplyCaptureAsync(messageId, "settlement-projection", captured));
        Assert.False(await ApplyCaptureAsync(messageId, "payment-audit", captured));
    }

    /// <summary>
    /// The staged retry schedule has to grow, not stay flat, or a failing message would
    /// be retried as fast as the broker can redeliver it.
    /// </summary>
    [Fact]
    public void The_retry_queue_names_follow_the_staged_delay_schedule()
    {
        string first = RabbitMqTopology.RetryQueueName(RabbitMqTopology.SettlementQueue, 1);
        string second = RabbitMqTopology.RetryQueueName(RabbitMqTopology.SettlementQueue, 2);
        string third = RabbitMqTopology.RetryQueueName(RabbitMqTopology.SettlementQueue, 3);

        Assert.Equal("flowpay.settlement.retry.5s", first);
        Assert.Equal("flowpay.settlement.retry.30s", second);
        Assert.Equal("flowpay.settlement.retry.120s", third);

        // An attempt past the end of the schedule clamps to the longest delay rather
        // than indexing out of range.
        Assert.Equal(third, RabbitMqTopology.RetryQueueName(RabbitMqTopology.SettlementQueue, 99));
    }

    private async Task<bool> ApplyCaptureAsync(
        Guid messageId,
        string consumer,
        PaymentCapturedEvent captured)
    {
        using IServiceScope scope = _fixture.Api.CreateScope();

        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        IInboxStore inbox = scope.ServiceProvider.GetRequiredService<IInboxStore>();
        SettlementProjectionStore projections =
            scope.ServiceProvider.GetRequiredService<SettlementProjectionStore>();

        await unitOfWork.BeginAsync();

        try
        {
            // Exactly what the consumer base class does: claim, then work, then commit.
            bool first = await inbox.TryBeginAsync(
                messageId, consumer, EventTypes.PaymentCaptured, deliveryAttempt: 1);

            if (!first)
            {
                await unitOfWork.RollbackAsync();
                return false;
            }

            if (consumer == "settlement-projection")
            {
                await projections.ApplyCaptureAsync(captured);
            }

            await unitOfWork.CommitAsync();
            return true;
        }
        catch
        {
            await unitOfWork.RollbackAsync();
            throw;
        }
    }

    private async Task<IReadOnlyList<OutboxMessage>> ReadPendingAsync()
    {
        using IServiceScope scope = _fixture.Api.CreateScope();

        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        IOutboxStore store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();

        // ClaimPendingAsync takes row locks, so it has to run inside a transaction that
        // is then rolled back to leave the rows available to the real dispatcher.
        await unitOfWork.BeginAsync();

        try
        {
            return await store.ClaimPendingAsync(batchSize: 1_000);
        }
        finally
        {
            await unitOfWork.RollbackAsync();
        }
    }

    private async Task<OutboxDepth> ReadDepthAsync()
    {
        using IServiceScope scope = _fixture.Api.CreateScope();
        IOutboxStore store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();

        return await store.GetDepthAsync();
    }

    /// <summary>
    /// Runs dispatch cycles until the backlog is clear, so assertions do not race a
    /// polling interval.
    /// </summary>
    private async Task DrainOutboxAsync()
    {
        OutboxDispatcher dispatcher = _fixture.Api.CreateOutboxDispatcher();

        for (int cycle = 0; cycle < 50; cycle++)
        {
            if (await dispatcher.DispatchOnceAsync(TestContext.Current.CancellationToken) == 0)
            {
                return;
            }
        }

        throw new InvalidOperationException("The outbox did not drain within 50 cycles.");
    }

    private static string NewKey() => $"it-{Guid.CreateVersion7():n}";
}
