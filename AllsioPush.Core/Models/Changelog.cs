namespace AllsioPush.Models;

public enum ChangeKind
{
    Feature,      // shown as "NEW"
    Improvement,  // shown as "IMPROVED"
    Fix,          // shown as "FIXED"
}

public class ChangeItem
{
    public ChangeKind Kind { get; init; }
    public string Text { get; init; } = string.Empty;

    public ChangeItem(ChangeKind kind, string text)
    {
        Kind = kind;
        Text = text;
    }
}

public class ChangelogRelease
{
    public string Version { get; init; } = string.Empty;
    public string Date { get; init; } = string.Empty; // human-readable, e.g. "June 2026"
    public IReadOnlyList<ChangeItem> Items { get; init; } = new List<ChangeItem>();
}

/// Curated, customer-facing changelog shown under Settings → What's New.
/// This is deliberately short and human-readable — NOT the full git changelog.
/// Add a new entry at the TOP for each release; keep wording plain and benefit-focused.
public static class Changelog
{
    public static readonly IReadOnlyList<ChangelogRelease> Releases = new List<ChangelogRelease>
    {
        new ChangelogRelease
        {
            Version = "2026.6.31",
            Date = "June 2026",
            Items = new List<ChangeItem>
            {
                new(ChangeKind.Fix, "Fixed a sign-in problem that could repeatedly reject correct credentials."),
                new(ChangeKind.Fix, "Notification history now requires sign-in and is hidden until you're signed in."),
                new(ChangeKind.Improvement, "Your notification history is tied to your account — it clears when you sign out and reloads when you sign back in."),
            },
        },
        new ChangelogRelease
        {
            Version = "2026.6.30",
            Date = "June 2026",
            Items = new List<ChangeItem>
            {
                new(ChangeKind.Improvement, "Updates now install quietly with a small progress notification instead of a pop-up dialog."),
            },
        },
        new ChangelogRelease
        {
            Version = "2026.6.29",
            Date = "June 2026",
            Items = new List<ChangeItem>
            {
                new(ChangeKind.Feature, "Choose where notifications appear on your screen — pick from six positions, including the corners and top/bottom center."),
            },
        },
        new ChangelogRelease
        {
            Version = "2026.6.28",
            Date = "June 2026",
            Items = new List<ChangeItem>
            {
                new(ChangeKind.Feature, "Text message (SMS) notifications — reply right from the popup, or jump straight to the conversation."),
                new(ChangeKind.Feature, "Personal & department SMS routing — only get notified about the conversations meant for you."),
                new(ChangeKind.Improvement, "Your notification history now shows SMS replies, including who responded."),
                new(ChangeKind.Fix, "Notifications no longer play a sound when they're filtered out by your SMS settings."),
                new(ChangeKind.Fix, "More reliable sign-in after changing your password."),
            },
        },
        new ChangelogRelease
        {
            Version = "2026.6.25",
            Date = "June 2026",
            Items = new List<ChangeItem>
            {
                new(ChangeKind.Feature, "Notification buttons can now open a link in your default browser."),
                new(ChangeKind.Fix, "The notification countdown bar is now easier to see."),
            },
        },
    };
}
