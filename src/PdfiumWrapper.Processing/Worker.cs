using System.Diagnostics;
using PdfiumWrapper.Processing.Protocol;

namespace PdfiumWrapper.Processing;

/// <summary>One worker process and the frames to and from it.</summary>
internal sealed class Worker : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Stream _toWorker;
    private readonly Stream _fromWorker;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource<HelloPayload> _hello = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action<Worker, Frame> _onFrame;
    private readonly Action<Worker, string> _onStderr;
    private readonly Action<Worker> _onExit;
    private Task? _readLoop;
    private int _exitSignalled;
    private int _disposed;

    private Worker(Process process, Action<Worker, Frame> onFrame, Action<Worker, string> onStderr, Action<Worker> onExit)
    {
        _process = process;
        _toWorker = process.StandardInput.BaseStream;
        _fromWorker = process.StandardOutput.BaseStream;
        _onFrame = onFrame;
        _onStderr = onStderr;
        _onExit = onExit;
    }

    public int Pid { get; private set; }
    public HelloPayload? Hello { get; private set; }
    public bool HasExited => _process.HasExited;

    /// <summary>Jobs this worker may run at once (<see cref="PdfPoolOptions.JobsPerWorker"/>).</summary>
    public int Slots { get; private set; } = 1;

    /// <summary>Slots taken: dispatched jobs plus claims the dispatcher holds. Guarded by the pool's worker lock.</summary>
    public int InUse;

    /// <summary>Jobs in flight on this worker, by id.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<long, PendingJob> Active { get; } = new();

    /// <summary>Stopwatch timestamp at which the worker last became idle.</summary>
    public long IdleSince { get; set; } = Stopwatch.GetTimestamp();

    /// <summary>Set once the pool has decided this worker goes away; no new jobs are dispatched to it.</summary>
    public bool Retiring { get; set; }

    public static async Task<Worker> StartAsync(PdfPoolOptions options, Action<Worker, Frame> onFrame,
        Action<Worker, string> onStderr, Action<Worker> onExit, CancellationToken ct)
    {
        var psi = WorkerLauncher.Create(options);
        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new PdfPoolException("Process.Start returned null for the worker.");
        }
        catch (Exception ex) when (ex is not PdfPoolException)
        {
            throw new PdfPoolException($"Could not start a worker from '{psi.FileName}': {ex.Message}", ex);
        }

        var worker = new Worker(process, onFrame, onStderr, onExit) { Pid = process.Id, Slots = options.JobsPerWorker };
        worker.Begin();

        using var startTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        startTimeout.CancelAfter(options.WorkerStartTimeout);
        try
        {
            worker.Hello = await worker._hello.Task.WaitAsync(startTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await worker.KillAsync().ConfigureAwait(false);
            throw new PdfPoolException($"Worker {worker.Pid} did not report ready within {options.WorkerStartTimeout}. " +
                                       "If the worker is this executable, Main must call PdfWorkerHost.TryRun() first.");
        }
        catch (PdfPoolException)
        {
            await worker.KillAsync().ConfigureAwait(false);
            throw;
        }

        if (worker.Hello.ProtocolVersion != Frame.ProtocolVersion)
        {
            await worker.KillAsync().ConfigureAwait(false);
            throw new PdfPoolException($"Worker {worker.Pid} speaks protocol {worker.Hello.ProtocolVersion}; this pool speaks {Frame.ProtocolVersion}.");
        }

        return worker;
    }

    private void Begin()
    {
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) => SignalExit();
        _readLoop = Task.Run(ReadLoopAsync);
        _ = Task.Run(DrainStderrAsync);
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                Frame? frame;
                try
                {
                    frame = await FrameStream.ReadAsync(_fromWorker, _lifetime.Token).ConfigureAwait(false);
                }
                catch (ProtocolException ex)
                {
                    // A worker that writes garbage to stdout cannot be trusted with the job in flight.
                    _hello.TrySetException(new PdfPoolException($"Worker {Pid} sent a malformed frame: {ex.Message}"));
                    _onStderr(this, "malformed frame from worker: " + ex.Message);
                    await KillAsync().ConfigureAwait(false);
                    break;
                }

                if (frame == null)
                    break;

                if (frame.Kind == FrameKind.Hello && frame.Hello != null)
                {
                    _hello.TrySetResult(frame.Hello);
                    continue;
                }

                _onFrame(this, frame);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _onStderr(this, "read loop ended: " + ex.Message);
        }
        finally
        {
            _hello.TrySetException(new PdfPoolException($"Worker {Pid} exited before reporting ready."));
            SignalExit();
        }
    }

    private async Task DrainStderrAsync()
    {
        try
        {
            while (await _process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
                _onStderr(this, line);
        }
        catch (Exception)
        {
        }
    }

    private void SignalExit()
    {
        if (Interlocked.Exchange(ref _exitSignalled, 1) == 0)
            _onExit(this);
    }

    public Task SendAsync(Frame frame, CancellationToken ct)
        => FrameStream.WriteAsync(_toWorker, frame, _writeLock, ct);

    public long WorkingSetBytes
    {
        get
        {
            try
            {
                _process.Refresh();
                return _process.HasExited ? 0 : _process.WorkingSet64;
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }

    /// <summary>Asks the worker to finish its current job and exit; kills it if it has not exited after <paramref name="grace"/>.</summary>
    public async Task StopAsync(TimeSpan grace)
    {
        try
        {
            await SendAsync(Frame.ForShutdown(), CancellationToken.None).ConfigureAwait(false);
            _toWorker.Close();
        }
        catch (Exception)
        {
        }

        try
        {
            using var cts = new CancellationTokenSource(grace);
            await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await KillAsync().ConfigureAwait(false);
        }
    }

    public async Task KillAsync()
    {
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        SignalExit();
    }

    public async ValueTask DisposeAsync()
    {
        // The pool's shutdown and the worker's own exit handler may both get here.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _lifetime.Cancel();
        await KillAsync().ConfigureAwait(false);
        if (_readLoop != null)
            await Task.WhenAny(_readLoop, Task.Delay(1000)).ConfigureAwait(false);
        _process.Dispose();
        _writeLock.Dispose();
        _lifetime.Dispose();
    }
}

/// <summary>Builds the start info for a worker process.</summary>
internal static class WorkerLauncher
{
    public static ProcessStartInfo Create(PdfPoolOptions options)
    {
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };

        if (options.WorkerPath != null)
        {
            if (options.WorkerPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                psi.FileName = "dotnet";
                psi.ArgumentList.Add(options.WorkerPath);
            }
            else
            {
                psi.FileName = options.WorkerPath;
            }

            foreach (var argument in options.WorkerArguments)
                psi.ArgumentList.Add(argument);
        }
        else
        {
            // Re-launch this process. Under "dotnet app.dll" the process path is the dotnet host and the
            // entry assembly must be passed again; under an apphost or a self-contained publish it is the app.
            string self = Environment.ProcessPath ?? throw new PdfPoolException("Cannot determine this process's executable; set WorkerPath.");
            psi.FileName = self;
            if (Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                string? entry = System.Reflection.Assembly.GetEntryAssembly()?.Location;
                if (string.IsNullOrEmpty(entry))
                    throw new PdfPoolException("Cannot determine the entry assembly to re-launch as a worker; set WorkerPath.");
                psi.ArgumentList.Add(entry);
            }
        }

        psi.Environment[PdfWorkerHost.EnvironmentVariable] = "1";
        psi.Environment[PdfWorkerHost.SlotsVariable] = options.JobsPerWorker.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var pair in options.WorkerEnvironment)
            psi.Environment[pair.Key] = pair.Value;

        return psi;
    }
}
