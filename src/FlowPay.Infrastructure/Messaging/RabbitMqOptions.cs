namespace FlowPay.Infrastructure.Messaging;

/// <summary>Broker connection and topology settings.</summary>
public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    public string Host { get; set; } = "localhost";

    public int Port { get; set; } = 5672;

    public string VirtualHost { get; set; } = "/";

    public string UserName { get; set; } = "guest";

    public string Password { get; set; } = "guest";

    /// <summary>
    /// Identifies this process in the broker's connection list.
    /// </summary>
    /// <remarks>
    /// Set per replica. During an incident, "which of the six workers is holding these
    /// unacked messages" is answerable from the management UI only if the connections are
    /// named.
    /// </remarks>
    public string ClientProvidedName { get; set; } = "flowpay";

    /// <summary>
    /// How many messages the broker may have outstanding per consumer.
    /// </summary>
    /// <remarks>
    /// Without a prefetch limit the broker pushes its entire backlog to the first
    /// consumer that connects, which both exhausts that process's memory and starves the
    /// other replicas. A modest window keeps work distributed and bounds the amount of
    /// in-flight state lost if a consumer dies.
    /// </remarks>
    public ushort PrefetchCount { get; set; } = 32;

    /// <summary>Handler attempts before a message is routed to the dead-letter queue.</summary>
    public int MaxDeliveryAttempts { get; set; } = 4;

    /// <summary>Seconds to wait before the broker retries a connection.</summary>
    public int NetworkRecoveryIntervalSeconds { get; set; } = 5;

    /// <summary>Whether to declare the topology at startup.</summary>
    public bool DeclareTopology { get; set; } = true;
}
