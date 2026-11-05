using System.Runtime.CompilerServices;

namespace FlowPay.Domain.Primitives;

/// <summary>
/// An ISO-4217 alphabetic currency code stored as three packed ASCII bytes inside a
/// single <see cref="uint"/>.
/// </summary>
/// <remarks>
/// Representing the code as a packed integer rather than a <see cref="string"/> keeps the
/// struct blittable, makes equality a single integer compare, and lets every currency
/// operation on the authorization path run without touching the managed heap. Only
/// <see cref="ToString"/> allocates, and it is deliberately excluded from the hot path.
/// </remarks>
public readonly struct Currency : IEquatable<Currency>
{
    // ISO-4217 codes are always three uppercase ASCII letters, so each one fits in a
    // little-endian uint with the fourth byte left as zero (the "unspecified" sentinel).
    private const uint UsdCode = (uint)('U' | ('S' << 8) | ('D' << 16));
    private const uint EurCode = (uint)('E' | ('U' << 8) | ('R' << 16));
    private const uint GbpCode = (uint)('G' | ('B' << 8) | ('P' << 16));
    private const uint InrCode = (uint)('I' | ('N' << 8) | ('R' << 16));
    private const uint JpyCode = (uint)('J' | ('P' << 8) | ('Y' << 16));
    private const uint AudCode = (uint)('A' | ('U' << 8) | ('D' << 16));
    private const uint CadCode = (uint)('C' | ('A' << 8) | ('D' << 16));
    private const uint ChfCode = (uint)('C' | ('H' << 8) | ('F' << 16));
    private const uint SgdCode = (uint)('S' | ('G' << 8) | ('D' << 16));
    private const uint AedCode = (uint)('A' | ('E' << 8) | ('D' << 16));

    private readonly uint _packed;

    private Currency(uint packed) => _packed = packed;

    public static Currency Usd => new(UsdCode);
    public static Currency Eur => new(EurCode);
    public static Currency Gbp => new(GbpCode);
    public static Currency Inr => new(InrCode);
    public static Currency Jpy => new(JpyCode);
    public static Currency Aud => new(AudCode);
    public static Currency Cad => new(CadCode);
    public static Currency Chf => new(ChfCode);
    public static Currency Sgd => new(SgdCode);
    public static Currency Aed => new(AedCode);

    /// <summary>The default value, which is not a valid settlement currency.</summary>
    public static Currency None => default;

    /// <summary>The raw packed representation, used by the persistence layer.</summary>
    public uint PackedValue
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _packed;
    }

    public bool IsNone
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _packed == 0;
    }

    /// <summary>
    /// True when FlowPay is configured to settle in this currency. Unknown but
    /// well-formed codes parse successfully and fail here, which keeps parsing and
    /// policy as separate concerns.
    /// </summary>
    public bool IsSupported
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _packed is UsdCode or EurCode or GbpCode or InrCode or JpyCode
            or AudCode or CadCode or ChfCode or SgdCode or AedCode;
    }

    /// <summary>
    /// Number of digits in the currency's minor unit (2 for USD cents, 0 for JPY).
    /// Needed to convert between the integer minor units FlowPay stores and the decimal
    /// amounts exposed over the API.
    /// </summary>
    public int MinorUnitDigits
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _packed switch
        {
            JpyCode => 0,
            _ => 2,
        };
    }

    /// <summary>
    /// The power of ten that converts a major-unit amount into minor units.
    /// </summary>
    public long MinorUnitScale
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _packed switch
        {
            JpyCode => 1L,
            _ => 100L,
        };
    }

    /// <summary>
    /// Parses a three-letter currency code without allocating. Returns false for any
    /// input that is not exactly three ASCII letters.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<char> code, out Currency currency)
    {
        if (code.Length != 3)
        {
            currency = default;
            return false;
        }

        uint c0 = Normalize(code[0]);
        uint c1 = Normalize(code[1]);
        uint c2 = Normalize(code[2]);

        // Normalize returns 0 for anything outside A-Z/a-z, so multiplying the three
        // results detects every malformed character with one branch.
        if ((c0 * c1 * c2) == 0)
        {
            currency = default;
            return false;
        }

        currency = new Currency(c0 | (c1 << 8) | (c2 << 16));
        return true;
    }

    /// <summary>
    /// Parses a code and requires it to be one FlowPay settles in.
    /// </summary>
    public static bool TryParseSupported(ReadOnlySpan<char> code, out Currency currency)
        => TryParse(code, out currency) && currency.IsSupported;

    /// <summary>Rebuilds a currency from its packed database representation.</summary>
    public static Currency FromPackedValue(uint packed) => new(packed);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Normalize(char c)
    {
        // Fold lowercase to uppercase, then reject anything that is not a letter.
        uint value = (uint)c;
        uint upper = value & ~0x20u;
        return (upper - 'A') <= ('Z' - 'A') ? upper : 0u;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(Currency other) => _packed == other._packed;

    public override bool Equals(object? obj) => obj is Currency other && Equals(other);

    public override int GetHashCode() => (int)_packed;

    public static bool operator ==(Currency left, Currency right) => left._packed == right._packed;

    public static bool operator !=(Currency left, Currency right) => left._packed != right._packed;

    /// <summary>
    /// Writes the code into <paramref name="destination"/> without allocating. Used by
    /// the serialization layer to render currencies into pooled buffers.
    /// </summary>
    public bool TryFormat(Span<char> destination, out int charsWritten)
    {
        if (destination.Length < 3 || _packed == 0)
        {
            charsWritten = 0;
            return false;
        }

        destination[0] = (char)(byte)_packed;
        destination[1] = (char)(byte)(_packed >> 8);
        destination[2] = (char)(byte)(_packed >> 16);
        charsWritten = 3;
        return true;
    }

    public override string ToString()
    {
        if (_packed == 0)
        {
            return "???";
        }

        return string.Create(3, _packed, static (span, packed) =>
        {
            span[0] = (char)(byte)packed;
            span[1] = (char)(byte)(packed >> 8);
            span[2] = (char)(byte)(packed >> 16);
        });
    }
}
