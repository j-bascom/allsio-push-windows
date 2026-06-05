using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;

namespace AllsioPush.UI.Windows;

internal interface IStackableToast
{
    int PixelWidth { get; }
    int PixelHeight { get; }
    bool IsClosing { get; }
    AppWindow AppWindow { get; }
    DispatcherQueue DispatcherQueue { get; }
}
