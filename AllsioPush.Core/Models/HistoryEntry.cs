namespace AllsioPush.Models;

public class HistoryEntry
{
    public string Id { get; set; } = string.Empty;
    public string? NotificationId { get; set; }
    public string TemplateType { get; set; } = "plain_text";
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string? ChannelName { get; set; }
    public string? GroupName { get; set; }
    public DateTime ReceivedAt { get; set; }
    public string? ActionTaken { get; set; }
    public DateTime? ActionTakenAt { get; set; }
    public string? AcknowledgedBy { get; set; }
    public bool IsRemoteAck { get; set; }
    public string? RawJson { get; set; }
}
