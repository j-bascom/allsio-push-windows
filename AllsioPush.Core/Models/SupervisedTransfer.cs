namespace AllsioPush.Models;

public class SupervisedTransferRequest
{
    public string ConnectionId { get; set; } = string.Empty;
    public string? CallId { get; set; }
    public string? CallerName { get; set; }
    public string? CallerPhone { get; set; }
    public string? Reason { get; set; }
    public string? AgentName { get; set; }
    public int TimeoutMs { get; set; } = 17000;
}

public class SupervisedTransferCancelled
{
    public string ConnectionId { get; set; } = string.Empty;
}
