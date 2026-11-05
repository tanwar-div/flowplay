namespace FlowPay.Domain.Primitives;

/// <summary>Identifies a FlowPay account (a balance-holding party).</summary>
public readonly record struct AccountId(Guid Value)
{
    /// <summary>
    /// Creates a new identifier using a version 7 UUID.
    /// </summary>
    /// <remarks>
    /// UUIDv7 is time-ordered, so freshly minted identifiers land at the right-hand edge
    /// of the primary-key B-tree instead of scattering across it. Under sustained insert
    /// load that is the difference between appending to a hot page and dirtying a random
    /// page per row.
    /// </remarks>
    public static AccountId New() => new(Guid.CreateVersion7());

    public static AccountId Empty => new(Guid.Empty);

    public bool IsEmpty => Value == Guid.Empty;

    public static bool TryParse(ReadOnlySpan<char> text, out AccountId id)
    {
        if (Guid.TryParse(text, out Guid value) && value != Guid.Empty)
        {
            id = new AccountId(value);
            return true;
        }

        id = default;
        return false;
    }

    public override string ToString() => Value.ToString("d");
}

/// <summary>Identifies a payment intent.</summary>
public readonly record struct PaymentId(Guid Value)
{
    public static PaymentId New() => new(Guid.CreateVersion7());

    public static PaymentId Empty => new(Guid.Empty);

    public bool IsEmpty => Value == Guid.Empty;

    public static bool TryParse(ReadOnlySpan<char> text, out PaymentId id)
    {
        if (Guid.TryParse(text, out Guid value) && value != Guid.Empty)
        {
            id = new PaymentId(value);
            return true;
        }

        id = default;
        return false;
    }

    public override string ToString() => Value.ToString("d");
}

/// <summary>Identifies a refund issued against a captured payment.</summary>
public readonly record struct RefundId(Guid Value)
{
    public static RefundId New() => new(Guid.CreateVersion7());

    public static RefundId Empty => new(Guid.Empty);

    public bool IsEmpty => Value == Guid.Empty;

    public override string ToString() => Value.ToString("d");
}

/// <summary>Identifies a single double-entry ledger line.</summary>
public readonly record struct LedgerEntryId(Guid Value)
{
    public static LedgerEntryId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("d");
}
