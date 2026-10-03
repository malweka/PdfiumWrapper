using System.Diagnostics;
using System.Text;
using PdfiumWrapper.Processing.Protocol;

namespace PdfiumWrapper.Processing;

/// <summary>One worker process and the frames to and from it.</summary>
internal sealed class Worker : IAsyncDisposable
{
    /// <summary>Longest standard error line passed on; the rest of a longer line is dropped.</summary>
    internal const int MaxStderrLineChars = 4096;

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

    /// <summary>Set while a job that must run alone holds this worker; no other job is dispatched to it. Guarded by the pool's worker lock.</summary>
    public bool Exclusive;

    /// <summary>Retired for memory: shut down once its last job has finished. Guarded by the pool's worker lock.</summary>
    public bool StopWhenDrained;

    /// <summary>Set once the pool has decided to kill this worker. Guarded by the pool's worker lock.</summary>
    public bool Killing;

    /// <summary>
    /// The job whose timeout or cancellation made the pool kill this worker, or 0. The other jobs
    /// on it were bystanders. Guarded by the pool's worker lock.
    /// </summary>
    public long KilledFor;

    /// <summary>
    /// Set once the pool has handled this worker's exit. The exit of a worker that dies right after
    /// reporting ready can be handled before its start adds it to the pool's table; the start sees
    /// this and does not add it. Guarded by the pool's worker lock.
    /// </summary>
    public bool ExitHandled;

    /// <param name="tempDirectory">The pool's own temp directory, where the worker writes large text results.</param>
    public static async Task<Worker> StartAsync(PdfPoolOptions options, string tempDirectory, Action<Worker, Frame> onFrame,
        Action<Worker, string> onStderr, Action<Worker> onExit, CancellationToken ct)
    {
        var psi = WorkerLauncher.Create(options, tempDirectory);
        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new PdfPoolException("Process.Start returned null for the worker.");
        }
        catch (Exception ex) when (ex is not PdfPoolException)
        {
            throw new PdfPoolException($"Could not start a worker from '{psi.FileName}': {ex.Message}", ex);
        }

        WorkerJobObject.Assign(process);

        var worker = new Worker(process, onFrame, onStderr, onExit) { Pid = process.Id, Slots = options.JobsPerWorker };
        bool ready = false;
        try
        {
            worker.Begin();

            using var startTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            startTimeout.CancelAfter(options.WorkerStartTimeout);
            try
            {
                worker.Hello = await worker._hello.Task.WaitAsync(startTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new PdfPoolException($"Worker {worker.Pid} did not report ready within {options.WorkerStartTimeout}. " +
                                           "If the worker is this executable, Main must call PdfWorkerHost.TryRun() first.");
            }

            if (worker.Hello.ProtocolVersion != Frame.ProtocolVersion)
                throw new PdfPoolException($"Worker {worker.Pid} speaks protocol {worker.Hello.ProtocolVersion}; this pool speaks {Frame.ProtocolVersion}.");

            ready = true;
            return worker;
        }
        finally
        {
            // Whatever ended the start (cancelled, timed out, failed, or threw anywhere above), the
            // child must not outlive this call: nothing else references it.
            if (!ready)
                await worker.KillAsync().ConfigureAwait(false);
        }
    }

    private void Begin()
    {
        _process.EnableRaisingEvents = true;
        _process.Exited += async (_, _) =>
        {
            // Frames the worker wrote before it died (progress, even a result) are still in the pipe;
            // let the read loop drain them before the exit is acted on, so the pool sees them first.
            if (_readLoop is { } readLoop)
                await Task.WhenAny(readLoop, Task.Delay(1000)).ConfigureAwait(false);
            SignalExit();
        };
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
                    await KillProcessAsync().ConfigureAwait(false);
                    break;   // the exit is signalled below, once this loop has ended
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

    /// <summary>
    /// Passes on each line the worker writes to standard error, cut at <see cref="MaxStderrLineChars"/>:
    /// a worker that writes without line breaks must not grow the pool's memory without bound.
    /// </summary>
    private async Task DrainStderrAsync()
    {
        var buffer = new char[1024];
        var line = new StringBuilder();
        bool truncated = false;
        try
        {
            int read;
            while ((read = await _process.StandardError.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
            {
                for (int i = 0; i < read; i++)
                {
                    char c = buffer[i];
                    if (c == '\n')
                        Emit();
                    else if (c == '\r')
                        continue;
                    else if (line.Length < MaxStderrLineChars)
                        line.Append(c);
                    else
                        truncated = true;
                }
            }

            if (line.Length > 0 || truncated)
                Emit();
        }
        catch (Exception)
        {
        }

        void Emit()
        {
            _onStderr(this, truncated ? line.Append(" [truncated]").ToString() : line.ToString());
            line.Clear();
            truncated = false;
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

    /// <summary>
    /// Kills the worker and signals its exit once the frames it wrote before dying have been read,
    /// as for any other exit: a result already in the pipe completes its job rather than being
    /// mistaken for a job still running.
    /// </summary>
    public async Task KillAsync()
    {
        await KillProcessAsync().ConfigureAwait(false);
        if (_readLoop is { } readLoop)
            await Task.WhenAny(readLoop, Task.Delay(1000)).ConfigureAwait(false);
        SignalExit();
    }

    private async Task KillProcessAsync()
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
    public static ProcessStartInfo Create(PdfPoolOptions options, string tempDirectory)
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
        psi.Environment[PdfWorkerHost.TempDirectoryVariable] = tempDirectory;
        foreach (var pair in options.WorkerEnvironment)
            psi.Environment[pair.Key] = pair.Value;

        return psi;
    }
}
