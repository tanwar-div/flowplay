using FlowPay.Domain.Payments;
using FlowPay.Domain.Primitives;
using Xunit;

namespace FlowPay.UnitTests.Domain;

public class FeeAccountShardTests
{
    private static readonly Guid UsdBase = Guid.Parse("00000000-0000-7000-8000-000001000000");
    private static readonly Guid EurBase = Guid.Parse("00000000-0000-7000-8000-000002000000");

    /// <summary>
    /// Shard ids stay readable: the index appears as the last hex digits, so an operator
    /// reconciling fee income can see which shard a row belongs to.
    /// </summary>
    [Theory]
    [InlineData(0, "00000000-0000-7000-8000-000001000000")]
    [InlineData(1, "00000000-0000-7000-8000-000001000001")]
    [InlineData(15, "00000000-0000-7000-8000-00000100000f")]
    [InlineData(255, "00000000-0000-7000-8000-0000010000ff")]
    public void Derive_writes_the_shard_index_into_the_id(int shard, string expected)
        => Assert.Equal(
            Guid.Parse(expected),
            FeeAccountShards.Derive(UsdBase, shard).Value);

    /// <summary>
    /// The families must not overlap, or two currencies would credit fees to the same
    /// account and the per-currency totals would be wrong.
    /// </summary>
    [Fact]
    public void Families_for_different_currencies_do_not_overlap()
    {
        HashSet<Guid> usd = [];
        HashSet<Guid> eur = [];

        for (int shard = 0; shard < FeeAccountShards.MaxShards; shard++)
        {
            usd.Add(FeeAccountShards.Derive(UsdBase, shard).Value);
            eur.Add(FeeAccountShards.Derive(EurBase, shard).Value);
        }

        Assert.Equal(FeeAccountShards.MaxShards, usd.Count);
        Assert.Equal(FeeAccountShards.MaxShards, eur.Count);
        Assert.Empty(usd.Intersect(eur));
    }

    [Fact]
    public void Derive_rejects_a_shard_index_past_the_limit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => FeeAccountShards.Derive(UsdBase, FeeAccountShards.MaxShards));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => FeeAccountShards.Derive(UsdBase, -1));
    }

    /// <summary>
    /// A retried capture has to land on the same shard, or one logical payment's fee
    /// would end up in different accounts depending on which attempt committed.
    /// </summary>
    [Fact]
    public void SelectShard_is_deterministic_for_a_payment()
    {
        PaymentId paymentId = PaymentId.New();

        int first = FeeAccountShards.SelectShard(paymentId, 16);
        int second = FeeAccountShards.SelectShard(paymentId, 16);

        Assert.Equal(first, second);
    }

    [Fact]
    public void SelectShard_stays_inside_the_shard_count()
    {
        for (int i = 0; i < 1_000; i++)
        {
            Assert.InRange(FeeAccountShards.SelectShard(PaymentId.New(), 16), 0, 15);
        }
    }

    [Fact]
    public void A_single_shard_always_selects_shard_zero()
    {
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(0, FeeAccountShards.SelectShard(PaymentId.New(), 1));
        }
    }

    /// <summary>
    /// The whole point of sharding is spreading load, so the selection has to actually
    /// spread. Consecutive UUIDv7 values share a timestamp prefix and differ only in
    /// their random tail, which is why the tail is what the selection reads.
    /// </summary>
    [Fact]
    public void SelectShard_distributes_consecutive_payment_ids_across_shards()
    {
        const int ShardCount = 16;
        const int Samples = 20_000;

        int[] hits = new int[ShardCount];

        for (int i = 0; i < Samples; i++)
        {
            hits[FeeAccountShards.SelectShard(PaymentId.New(), ShardCount)]++;
        }

        Assert.All(hits, count => Assert.True(
            count > 0, "Every shard should receive traffic."));

        // A uniform spread would give Samples/ShardCount per shard. Allowing a 2x band
        // either side keeps the test stable while still failing a selection that clusters.
        int expected = Samples / ShardCount;

        Assert.All(hits, count => Assert.InRange(count, expected / 2, expected * 2));
    }

    [Fact]
    public void DeriveAll_fills_the_family_in_order()
    {
        AccountId[] family = new AccountId[8];
        int written = FeeAccountShards.DeriveAll(UsdBase, 8, family);

        Assert.Equal(8, written);

        for (int shard = 0; shard < 8; shard++)
        {
            Assert.Equal(FeeAccountShards.Derive(UsdBase, shard), family[shard]);
        }
    }

    [Fact]
    public void DeriveAll_respects_a_buffer_smaller_than_the_family()
    {
        AccountId[] small = new AccountId[3];

        Assert.Equal(3, FeeAccountShards.DeriveAll(UsdBase, 16, small));
    }
}
