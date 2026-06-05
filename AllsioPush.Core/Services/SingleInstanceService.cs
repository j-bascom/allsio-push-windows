using System.IO.Pipes;
using System.Text;

namespace AllsioPush.Services;

public class SingleInstanceService : IDisposable
{
    private const string MutexName = "AllsioPush_SingleInstance_Mutex";
    private const string PipeName = "AllsioPush_IPC";

    private Mutex? _mutex;
    private bool _isFirstInstance;
    private CancellationTokenSource? _cts;
    private bool _mutexOwned;

    public bool IsFirstInstance => _isFirstInstance;

    public event Action<string[]>? OnSecondInstanceArgs;

    private readonly SynchronizationContext _uiContext;

    public SingleInstanceService(SynchronizationContext uiContext)
    {
        _uiContext = uiContext;
        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out _isFirstInstance);
            _mutexOwned = _isFirstInstance;
        }
        catch (AbandonedMutexException)
        {
            // Previous owner crashed without releasing — we inherit ownership.
            _isFirstInstance = true;
            _mutexOwned = true;
        }

        // Mutex already existed (createdNew=false). Try a zero-timeout acquire to
        // distinguish an abandoned/unowned mutex from one held by a live instance.
        if (!_isFirstInstance && _mutex != null)
        {
            try
            {
                _isFirstInstance = _mutex.WaitOne(0);
                _mutexOwned = _isFirstInstance;
            }
            catch (AbandonedMutexException)
            {
                _isFirstInstance = true;
                _mutexOwned = true;
            }
        }
    }

    public void StartPipeServer()
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => PipeServerLoop(_cts.Token));
    }

    public static void SendArgsToRunningInstance(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeout: 2000);
            var payload = string.Join("\n", args);
            var bytes = Encoding.UTF8.GetBytes(payload);
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[IPC] Failed to send args to running instance: {ex.Message}");
        }
    }

    private async Task PipeServerLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    transmissionMode: PipeTransmissionMode.Byte,
                    options: PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct);

                using var ms = new MemoryStream();
                await server.CopyToAsync(ms, ct);
                var payload = Encoding.UTF8.GetString(ms.ToArray());
                var args = payload.Split('\n', StringSplitOptions.RemoveEmptyEntries);

                if (args.Length > 0)
                {
                    _uiContext.Post(_ =>
                    {
                        try { OnSecondInstanceArgs?.Invoke(args); }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[IPC] Handler threw: {ex.Message}");
                        }
                    }, null);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[IPC] Pipe server error: {ex.Message}");
                try { await Task.Delay(1000, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        _cts?.Dispose();
        if (_mutex != null)
        {
            try { if (_mutexOwned) _mutex.ReleaseMutex(); } catch { }
            _mutex.Dispose();
            _mutex = null;
        }
    }
}
