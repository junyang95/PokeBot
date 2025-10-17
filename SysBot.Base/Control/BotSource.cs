using System.Threading;
using System.Threading.Tasks;

namespace SysBot.Base;

public class BotSource<T>(RoutineExecutor<T> Bot)
    where T : class, IConsoleBotConfig
{
    public readonly RoutineExecutor<T> Bot = Bot;

    private CancellationTokenSource Source = new();

    public bool IsPaused { get; private set; }

    public bool IsRunning { get; private set; }

    public bool IsStopping { get; set; }

    public void Pause()
    {
        if (!IsRunning || IsStopping)
            return;

        IsPaused = true;
        Task.Run(Bot.SoftStop)
            .ContinueWith(ReportFailure, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously)
            .ContinueWith(_ => IsPaused = false, TaskContinuationOptions.OnlyOnFaulted);
    }

    public void RebootAndStop()
    {
        Stop();

        Task.Run(() => Bot.RebootAndStopAsync(Source.Token)
            .ContinueWith(ReportFailure, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously)
            .ContinueWith(_ => IsRunning = false));

        IsRunning = true;
    }

    public void Restart()
    {
        bool ok = true;
        Task.Run(Bot.Connection.Reset).ContinueWith(task =>
        {
            ok = false;
            ReportFailure(task);
        }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously)
        .ContinueWith(_ =>
        {
            if (ok)
                Start();
        }, TaskContinuationOptions.RunContinuationsAsynchronously | TaskContinuationOptions.NotOnFaulted);
    }

    public void Resume()
    {
        Start();
    }

    public void Start()
    {
        if (IsPaused)
            Stop(); // can't soft-resume; just re-launch

        if (IsRunning || IsStopping)
            return;

        IsRunning = true;
        Task.Run(async () => await Bot.RunAsync(Source.Token)
            .ContinueWith(ReportFailure, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously)
            .ContinueWith(_ => IsRunning = false));
    }

    public void Stop()
    {
        if (!IsRunning || IsStopping)
            return;

        IsStopping = true;
        Source.Cancel();
        Source.Dispose(); // Dispose the old CancellationTokenSource to prevent memory leak
        Source = new CancellationTokenSource();

        Task.Run(async () => await Bot.HardStop()
            .ContinueWith(ReportFailure, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously)
            .ContinueWith(_ => IsPaused = IsRunning = IsStopping = false));
    }

    private void ReportFailure(Task finishedTask)
    {
        var ident = Bot.Connection.Name;
        var ae = finishedTask.Exception;
        if (ae == null)
        {
            LogUtil.LogError("Bot has stopped without error.", ident);
            return;
        }

        LogUtil.LogError("Bot has crashed!", ident);

        if (!string.IsNullOrEmpty(ae.Message))
            LogUtil.LogError("Aggregate message: " + ae.Message, ident);

        var st = ae.StackTrace;
        if (!string.IsNullOrEmpty(st))
            LogUtil.LogError("Aggregate stacktrace: " + st, ident);

        foreach (var e in ae.InnerExceptions)
        {
            if (!string.IsNullOrEmpty(e.Message))
                LogUtil.LogError("Inner message: " + e.Message, ident);
            LogUtil.LogError("Inner stacktrace: " + e.StackTrace, ident);
        }
    }
}
