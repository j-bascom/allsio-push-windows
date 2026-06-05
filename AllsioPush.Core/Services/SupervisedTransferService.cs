using AllsioPush.Config;
using AllsioPush.Models;
using Microsoft.Toolkit.Uwp.Notifications;
using Windows.UI.Notifications;

namespace AllsioPush.Services;

public class SupervisedTransferService
{
    private readonly AppSettings _settings;
    private readonly AckService _ackService;

    private readonly object _lock = new();
    private readonly Dictionary<string, ActiveTransfer> _active = new();

    private sealed class ActiveTransfer
    {
        public required string Tag { get; init; }
        public required int TimeoutMs { get; init; }
        public int ElapsedMs { get; set; }
        public System.Threading.Timer? Timer { get; set; }
    }

    public SupervisedTransferService(AppSettings settings, AckService ackService)
    {
        _settings = settings;
        _ackService = ackService;
    }

    public void HandleTransferRequest(SupervisedTransferRequest req)
    {
        HandleCancellation(req.ConnectionId);
        var tag = SanitizeTag($"stx_{req.ConnectionId}");
        ShowTransferToast(req, tag);
        StartCountdownTimer(req.ConnectionId, req.TimeoutMs, tag);
    }

    public void HandleCancellation(string connectionId)
    {
        ActiveTransfer? transfer;
        lock (_lock)
        {
            if (!_active.TryGetValue(connectionId, out transfer)) return;
            _active.Remove(connectionId);
        }
        transfer.Timer?.Dispose();
        DismissTransferToast(transfer.Tag);
    }

    private static void ShowTransferToast(SupervisedTransferRequest req, string tag)
    {
        try
        {
            var title = string.IsNullOrWhiteSpace(req.AgentName)
                ? "Incoming Transfer"
                : $"Transfer from {req.AgentName}";

            var callerLine = req.CallerName ?? req.CallerPhone ?? "Unknown Caller";

            var acceptArgs = $"action=transfer_accept;connectionId={Uri.EscapeDataString(req.ConnectionId)}";
            var declineArgs = $"action=transfer_decline;connectionId={Uri.EscapeDataString(req.ConnectionId)}";

            var binding = new ToastBindingGeneric();
            binding.Children.Add(new AdaptiveText { Text = title, HintStyle = AdaptiveTextStyle.Base });
            binding.Children.Add(new AdaptiveText { Text = callerLine });
            binding.Children.Add(new AdaptiveProgressBar
            {
                Value = new BindableProgressBarValue("progressValue"),
                Status = string.IsNullOrWhiteSpace(req.Reason) ? "" : req.Reason,
            });

            var content = new ToastContent
            {
                Visual = new ToastVisual { BindingGeneric = binding },
                Actions = new ToastActionsCustom
                {
                    Buttons =
                    {
                        new ToastButton("Accept", acceptArgs)
                        {
                            ActivationType = ToastActivationType.Background,
                        },
                        new ToastButton("Decline", declineArgs)
                        {
                            ActivationType = ToastActivationType.Background,
                        },
                    },
                },
                Audio = new ToastAudio
                {
                    Src = new Uri("ms-winsoundevent:Notification.Looping.Alarm2"),
                    Loop = true,
                },
            };

            var toast = new ToastNotification(content.GetXml())
            {
                Tag = tag,
                Group = "supervised_transfer",
                Data = new NotificationData
                {
                    Values = { ["progressValue"] = "1" },
                },
                ExpirationTime = DateTimeOffset.Now.AddMilliseconds(req.TimeoutMs + 2000),
            };

            ToastNotificationManagerCompat.CreateToastNotifier().Show(toast);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Transfer] ShowTransferToast failed: {ex.Message}");
        }
    }

    private void StartCountdownTimer(string connectionId, int timeoutMs, string tag)
    {
        var transfer = new ActiveTransfer { Tag = tag, TimeoutMs = timeoutMs };
        lock (_lock) { _active[connectionId] = transfer; }

        const int tickMs = 500;
        transfer.Timer = new System.Threading.Timer(
            _ => OnCountdownTick(connectionId, tickMs),
            null, tickMs, tickMs);
    }

    private void OnCountdownTick(string connectionId, int tickMs)
    {
        ActiveTransfer? transfer;
        lock (_lock)
        {
            if (!_active.TryGetValue(connectionId, out transfer)) return;
            transfer.ElapsedMs += tickMs;
        }

        var remaining = transfer.TimeoutMs - transfer.ElapsedMs;
        var ratio = Math.Max(0.0, (double)remaining / transfer.TimeoutMs);

        var data = new NotificationData();
        data.Values["progressValue"] = ratio.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);

        try
        {
            var result = ToastNotificationManagerCompat.CreateToastNotifier()
                .Update(data, transfer.Tag, "supervised_transfer");

            if (result == NotificationUpdateResult.Failed)
            {
                lock (_lock) { _active.Remove(connectionId); }
                transfer.Timer?.Dispose();
                return;
            }
        }
        catch { }

        if (remaining <= 0)
        {
            lock (_lock) { _active.Remove(connectionId); }
            transfer.Timer?.Dispose();
            DismissTransferToast(transfer.Tag);
        }
    }

    private static void DismissTransferToast(string tag)
    {
        try { ToastNotificationManagerCompat.History.Remove(tag, "supervised_transfer"); }
        catch { }
    }

    public void AcceptTransfer(string connectionId)
    {
        HandleCancellation(connectionId);
        var url = $"{_settings.ApiBase}/api/supervised-transfer/{Uri.EscapeDataString(connectionId)}/accept";
        _ = _ackService.PostTransferAction(url);
    }

    public void DeclineTransfer(string connectionId)
    {
        HandleCancellation(connectionId);
        var url = $"{_settings.ApiBase}/api/supervised-transfer/{Uri.EscapeDataString(connectionId)}/decline";
        _ = _ackService.PostTransferAction(url);
    }

    private static string SanitizeTag(string id) => id.Length <= 64 ? id : id[..64];
}
