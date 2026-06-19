using AllsioPush.Models;

namespace AllsioPush.Services;

/// A window that can be remotely acknowledged (someone else acked the same notification).
public interface IRemoteAckTarget
{
    string? NotificationId { get; }
    void RemoteAcknowledged(string acknowledgedBy);
}

/// A screen-pop window tied to a live call; closed when the call ends.
public interface IScreenPopTarget
{
    string? CallId { get; }
    void CallEnded();
}

/// An SMS slideout that can be remotely acknowledged when someone else
/// replies to / acknowledges the same conversation.
public interface ISmsAckTarget
{
    string? ConversationId { get; }
    void RemoteAcknowledged(string acknowledgedBy, string? actionType);
}

/// Abstraction the router uses to surface UI without depending on the App layer.
/// The App implements this; calls arrive already marshalled to the UI thread.
public interface INotificationPresenter
{
    void ShowPopup(PushNotification notification);
    void ShowSlideout(PushNotification notification);
    void ShowSmsSlideout(PushNotification notification, bool startExpanded);
    void ShowScreenPop(ScreenPopData data);
    void OpenUrl(string url);
}
