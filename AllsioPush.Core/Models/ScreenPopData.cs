namespace AllsioPush.Models;

public class ScreenPopData
{
    // Core caller info
    public string? CallerName { get; set; }
    public string? CallerPhone { get; set; }
    public string? Reason { get; set; }
    public string? AgentName { get; set; }
    public string? CallId { get; set; }
    public DateTime CallStarted { get; set; } = DateTime.Now;

    // Phorest match (salon/spa customers)
    public PhorestRecord? Phorest { get; set; }

    // QBO match (business customers)
    public QboRecord? Qbo { get; set; }

    // Prior call history (last 5)
    public List<PriorCall> PriorCalls { get; set; } = new();

    // Raw notification payload for fallback rendering
    public PushNotification? SourceNotification { get; set; }
}

public class PhorestRecord
{
    public string? ClientId { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? LastVisitDate { get; set; }
    public string? LastService { get; set; }
    public string? PreferredStylist { get; set; }
    public string? Notes { get; set; }
    public int? TotalVisits { get; set; }
    public decimal? LifetimeValue { get; set; }
}

public class QboRecord
{
    public string? CustomerId { get; set; }
    public string? DisplayName { get; set; }
    public string? CompanyName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? BillingAddress { get; set; }
    public decimal? Balance { get; set; }
}

public class PriorCall
{
    public DateTime CallDate { get; set; }
    public string? Duration { get; set; }
    public string? Reason { get; set; }
    public string? Outcome { get; set; }
    public string? AgentName { get; set; }
}
