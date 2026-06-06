using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace AllsioPush.UI.Windows;

internal interface IStackableToast
{
    int PixelWidth { get; }
    int PixelHeight { get; }
    bool IsClosing { get; }
    AppWindow AppWindow { get; }
    DispatcherQueue DispatcherQueue { get; }
    void MoveTo(PointInt32 target);
}
