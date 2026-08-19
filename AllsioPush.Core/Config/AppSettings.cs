namespace AllsioPush.Config;

public enum ServerEnvironment
{
    Production,
    Development
}

// Where notification slideouts anchor on screen. Order matters: the
// Settings dropdown is built from these in declaration order.
public enum NotificationAnchor
{
    BottomRight,
    BottomLeft,
    TopRight,
    TopLeft,
    MiddleRight,
    MiddleLeft
}

// How a notification enters and leaves the screen.
public enum NotificationAnimation
{
    Slide,
    Fade
}

public class AppSettings
{
    public ServerEnvironment Environment { get; set; } = ServerEnvironment.Production;
    public bool SoundEnabled { get; set; } = true;
    public bool DeferToToast { get; set; } = false;
    public bool LaunchOnStartup { get; set; } = true;
    public NotificationAnchor NotificationLocation { get; set; } = NotificationAnchor.BottomRight;
    public NotificationAnimation NotificationAnimation { get; set; } = NotificationAnimation.Slide;

    // How long an SMS slideout stays on screen, in seconds. 0 = stay until
    // actioned, which is the historical behaviour and remains the default —
    // an SMS vanishing on its own loses a message the user may not have read.
    // The countdown pauses while the reply panel is open (see SmsSlideoutWindow).
    public int SmsTtlSeconds { get; set; }

    // The app version that last ran on this machine. Used to detect a completed
    // update on the next launch so we can show a one-off "upgrade complete"
    // notice. Null on a fresh install (no notice shown then).
    public string? LastRunVersion { get; set; }

    public string ApiBase => Environment == ServerEnvironment.Production
        ? "https://sync.charlestontel.com"
        : "https://dev-sync.charlestontel.com";

    public string AdminBase => Environment == ServerEnvironment.Production
        ? "https://ai.charlestontel.com"
        : "https://dev-ai.charlestontel.com";
}
