using AllsioPush.Config;
using Windows.Graphics;

namespace AllsioPush.UI.Windows;

// Centralizes the geometry for where notification slideouts anchor and how
// they slide in/out. All three toast window types (SlideoutWindow,
// SmsSlideoutWindow, UpdateToastWindow) read from here so a single anchor
// setting drives every position. The active anchor is a process-wide static,
// updated at startup and whenever the user changes it in Settings.
internal static class ToastLayout
{
    internal const int Margin = 12;
    internal const int Gap = 8;

    // Process-wide current anchor. Set from the loaded AppSettings at startup
    // and again whenever the Settings dropdown changes it.
    internal static NotificationAnchor Anchor = NotificationAnchor.BottomRight;

    // Entrance/exit style, same lifecycle as Anchor above. Windows hosting a
    // WebView2 ignore this and always slide: they never enable the layered
    // window style that alpha blending needs.
    internal static NotificationAnimation Animation = NotificationAnimation.Slide;

    // Left-edge anchors slide in/out on the left; everything else on the right.
    internal static bool IsLeft(NotificationAnchor a) =>
        a is NotificationAnchor.TopLeft
          or NotificationAnchor.BottomLeft
          or NotificationAnchor.MiddleLeft;

    // Top anchors grow the stack downward; bottom and middle anchors grow upward.
    internal static bool GrowsDown(NotificationAnchor a) =>
        a is NotificationAnchor.TopLeft or NotificationAnchor.TopRight;

    internal static bool IsMiddle(NotificationAnchor a) =>
        a is NotificationAnchor.MiddleLeft or NotificationAnchor.MiddleRight;

    // Resting X for a toast of the given pixel width — flush to the left or
    // right edge of the work area, inside the margin.
    internal static int RestX(RectInt32 area, int pixelWidth) =>
        IsLeft(Anchor)
            ? area.X + Margin
            : area.X + area.Width - pixelWidth - Margin;

    // Off-screen X used as the slide-in start and slide-out end: just past the
    // left edge for left anchors, just past the right edge otherwise.
    internal static int OffScreenX(RectInt32 area, int pixelWidth) =>
        IsLeft(Anchor)
            ? area.X - pixelWidth
            : area.X + area.Width;

    // Top Y for the newest toast of the given height, ignoring any stacking —
    // used when a single window needs to settle to its anchor (e.g. expand).
    internal static int NewestTop(RectInt32 area, int pixelHeight)
    {
        if (GrowsDown(Anchor)) return area.Y + Margin;
        var baseline = IsMiddle(Anchor)
            ? area.Y + area.Height / 2
            : area.Y + area.Height - Margin;
        return baseline - pixelHeight;
    }
}
