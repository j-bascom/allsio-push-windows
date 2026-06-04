namespace AllsioPush.Models;

public class PushNotification
{
    public string? NotificationId { get; set; }
    public string TemplateType { get; set; } = "plain_text";
    public string DisplayMode { get; set; } = "slideout";
    public string Title { get; set; } = "Notification";
    public string Content { get; set; } = string.Empty;
    public string? Url { get; set; }
    public string? CustomHtml { get; set; }
    public string? HeaderColor { get; set; }
    public string? Sound { get; set; }
    public int? Ttl { get; set; }
    public int? PopupWidth { get; set; }
    public int? PopupHeight { get; set; }
    public string? ChannelName { get; set; }
    public string? GroupName { get; set; }
    public List<NotificationButton> Buttons { get; set; } = new();

    public string? CallerName { get; set; }
    public string? CallerPhone { get; set; }
    public string? Reason { get; set; }

    public string? AppointmentDate { get; set; }
    public string? Service { get; set; }
    public string? Stylist { get; set; }

    public string? SenderName { get; set; }
    public string? SenderPhone { get; set; }
    public string? ConversationId { get; set; }

    // Scalar payload fields not mapped to a dedicated property (e.g. callId,
    // agentName, phorest_*/qbo_* CRM fields). Keyed by their original JSON name.
    public Dictionary<string, string?> Extras { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Prior call history carried by caller_card notifications.
    public List<PriorCall> PriorCalls { get; set; } = new();
}

public class NotificationButton
{
    public string Label { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Style { get; set; } = "default";
    public string? WebhookUrl { get; set; }
}
