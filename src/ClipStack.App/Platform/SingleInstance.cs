using System;
using System.IO;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace ClipStack.App.Platform;

internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex mutex;
    private readonly string pipeName;
    private readonly CancellationTokenSource cancellation = new();
    private readonly bool owned;
    private bool disposed;
    public bool IsPrimary => owned;

    public SingleInstance()
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        string sid = identity.User?.Value ?? Environment.UserName;
        pipeName = "ClipStack-" + sid + "-" + process.SessionId;
        mutex = new Mutex(true, @"Local\" + pipeName, out owned);
    }

    public async Task SignalAsync()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(5000);
            await client.WriteAsync(new byte[] { 1 });
        }
        catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException) { }
    }

    public async Task ListenAsync(Action activate)
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellation.Token);
                byte[] signal = new byte[1];
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                timeout.CancelAfter(1500);
                if (await server.ReadAsync(signal, timeout.Token) > 0) activate();
            }
            catch (OperationCanceledException) { }
            catch (IOException) { await Task.Delay(100); }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        cancellation.Cancel();
        if (owned) mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
