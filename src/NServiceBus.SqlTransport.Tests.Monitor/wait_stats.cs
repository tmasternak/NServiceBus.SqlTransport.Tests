using System.Diagnostics;

struct wait_stats
{
    public double waiting_tasks_count;
    public double wait_time_s;
    public double signal_wait_time_s;
    public long max_wait_time_ms;
    public long wait_per_task_ms;
    public long ticks;

    public wait_stats Subtract(wait_stats instance)
    {
        float ticksDelta = ticks - instance.ticks;
        var ticksDeltaFactor = Stopwatch.Frequency / ticksDelta;
        return new wait_stats
        {
            max_wait_time_ms = max_wait_time_ms,
            wait_per_task_ms = wait_per_task_ms,

            signal_wait_time_s = (signal_wait_time_s - instance.signal_wait_time_s) * ticksDeltaFactor,
            waiting_tasks_count = (waiting_tasks_count - instance.waiting_tasks_count) * ticksDeltaFactor,
            wait_time_s = (wait_time_s - instance.wait_time_s) * ticksDeltaFactor,

            ticks = (long)ticksDelta
        };
    }

    public override string ToString()
    {
        return $"waiting_tasks_count:{waiting_tasks_count,6:N}   wait_time_s:{wait_time_s,7:N3}   max_wait_time_ms:{max_wait_time_ms,6:N}   signal_wait_time_s:{signal_wait_time_s,6:N}   wait_per_task_ms:{wait_per_task_ms,6:N}   ticks:{1000 * ticks / Stopwatch.Frequency,6:N0}ms";
    }

}