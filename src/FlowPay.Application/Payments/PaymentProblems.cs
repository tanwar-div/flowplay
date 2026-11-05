using System.Text.Json;
using FlowPay.Domain.Payments;
using FlowPay.Domain.Primitives;

namespace FlowPay.Application.Payments;

/// <summary>Builds the serialized error bodies the handlers return.</summary>
public static class PaymentProblems
{
    /// <summary>
    /// Maps a domain rejection onto an HTTP status code.
    /// </summary>
    /// <remarks>
    /// The split is about whether the client can do anything useful with the answer.
    /// 422 means the request itself is wrong and resending it unchanged will fail the
    /// same way. 409 means the request is well formed but conflicts with the resource's
    /// current state, so re-reading and retrying may succeed. 500 is reserved for
    /// outcomes that indicate a bug in FlowPay rather than a problem with the request.
    /// </remarks>
    public static int StatusFor(DomainError error) => error switch
    {
        DomainError.InsufficientFunds => 422,
        DomainError.AccountNotActive => 422,
        DomainError.CurrencyMismatch => 422,
        DomainError.AmountNotPositive => 422,
        DomainError.AmountExceedsLimit => 422,
        DomainError.RefundExceedsCapturedAmount => 422,
        DomainError.ReservationExceeded => 409,
        DomainError.PaymentNotCaptured => 409,
        DomainError.TransitionNotPermitted => 409,
        DomainError.TerminalState => 409,
        DomainError.ConcurrencyConflict => 409,
        DomainError.UnknownState => 500,
        DomainError.ArithmeticOverflow => 500,
        _ => 400,
    };

    public static OperationResponseBody ForDomainError(DomainError error)
    {
        int status = StatusFor(error);

        ProblemDto problem = new(
            DomainErrors.Code(error),
            DomainErrors.Message(error),
            status);

        return new OperationResponseBody(status, Serialize(problem));
    }

    public static OperationResponseBody ForValidation(PaymentValidationResult validation)
    {
        // Sized to the number of defined failure flags. Allocated on the heap rather
        // than the stack because the codes are strings, and this is the rejection path
        // anyway, where an allocation costs nothing that matters.
        string[] codes = new string[validation.FailureCount];
        int count = validation.GetErrorCodes(codes);

        string[] errors = count == codes.Length ? codes : codes[..count];

        ProblemDto problem = new(
            "validation_failed",
            "The payment request is not valid.",
            422,
            Detail: null,
            Errors: errors);

        return new OperationResponseBody(422, Serialize(problem));
    }

    public static OperationResponseBody NotFound(string resource)
    {
        ProblemDto problem = new(
            "not_found",
            $"The requested {resource} does not exist.",
            404);

        return new OperationResponseBody(404, Serialize(problem));
    }

    public static OperationResponseBody BadRequest(string type, string title)
        => new(400, Serialize(new ProblemDto(type, title, 400)));

    public static OperationResponseBody Unprocessable(string type, string title)
        => new(422, Serialize(new ProblemDto(type, title, 422)));

    private static string Serialize(ProblemDto problem)
        => JsonSerializer.Serialize(problem, FlowPayJsonContext.Default.ProblemDto);
}

/// <summary>A status code and its serialized body.</summary>
public readonly record struct OperationResponseBody(int StatusCode, string Json);
