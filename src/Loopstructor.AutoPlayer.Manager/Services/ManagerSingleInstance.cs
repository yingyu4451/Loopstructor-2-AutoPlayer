using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Loopstructor.AutoPlayer.Manager.Services;

internal sealed class ManagerSingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly bool _ownsMutex;
    private Task? _listener;

    private ManagerSingleInstance(Mutex mutex, bool ownsMutex, string pipeName)
    {
        _mutex = mutex;
        _ownsMutex = ownsMutex;
        _pipeName = pipeName;
    }

    public bool IsPrimary => _ownsMutex;

    public static ManagerSingleInstance Create()
    {
        string identity = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        string suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).Substring(0, 16);
        string mutexName = "Local\\Loopstructor.AutoPlayer.Manager." + suffix;
        string pipeName = "Loopstructor.AutoPlayer.Manager.Activate." + suffix;
        Mutex mutex = new(true, mutexName, out bool createdNew);
        return new ManagerSingleInstance(mutex, createdNew, pipeName);
    }

    public void StartListening(Action activate)
    {
        if (!_ownsMutex || _listener != null) return;
        _listener = Task.Run(async () =>
        {
            while (!_lifetime.IsCancellationRequested)
            {
                try
                {
                    await using NamedPipeServerStream server = new(
                        _pipeName,
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(_lifetime.Token).ConfigureAwait(false);
                    activate();
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                    return;
                }
                catch
                {
                    await Task.Delay(250, _lifetime.Token).ConfigureAwait(false);
                }
            }
        });
    }

    public bool NotifyPrimary()
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using NamedPipeClientStream client = new(".", _pipeName, PipeDirection.Out, PipeOptions.None);
                client.Connect(500);
                return true;
            }
            catch
            {
                Thread.Sleep(120);
            }
        }

        return false;
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        if (_ownsMutex)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        }
        _mutex.Dispose();
        _lifetime.Dispose();
    }
}
