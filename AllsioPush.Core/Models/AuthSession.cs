namespace AllsioPush.Models;

public class AuthSession
{
    public string Token { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string PusherAppKey { get; set; } = string.Empty;
    public string PusherCluster { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string PersonalChannel { get; set; } = string.Empty;
    public List<PushGroup> PushGroups { get; set; } = new();
    public string? EncryptionKey { get; set; }

    // Which SMS conversations this user is notified about: "personal_only",
    // "mine" (personal + department), or "all". Drives scope filtering of
    // sms-template notifications. Defaults to "mine".
    public string SmsNotificationScope { get; set; } = "mine";
}

public class PushGroup
{
    public string Name { get; set; } = string.Empty;
    public string PusherChannel { get; set; } = string.Empty;
}
