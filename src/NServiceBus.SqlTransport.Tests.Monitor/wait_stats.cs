using System.Diagnostics;

struct wait_stats
{
    public double waiting_tasks_count;
    public double wait_time_s;
    public double signal_wait_time_s;

    public double waiting_tasks_count_raw;
    public double wait_time_s_raw;
    public double signal_wait_time_s_raw;

    public long max_wait_time_ms;
    public long wait_per_task_ms;
    public long ticks;

    public wait_stats Subtract(wait_stats instance)
    {
        double ticksDelta = ticks - instance.ticks;
        var ticksDeltaFactor = Stopwatch.Frequency / ticksDelta;
        var result = new wait_stats
        {
            max_wait_time_ms = max_wait_time_ms,
            wait_per_task_ms = wait_per_task_ms,

            signal_wait_time_s_raw = signal_wait_time_s - instance.signal_wait_time_s,
            waiting_tasks_count_raw = waiting_tasks_count - instance.waiting_tasks_count,
            wait_time_s_raw = wait_time_s - instance.wait_time_s,

            ticks = (long)ticksDelta
        };

        result.signal_wait_time_s = result.signal_wait_time_s_raw * ticksDeltaFactor;
        result.waiting_tasks_count = result.waiting_tasks_count_raw * ticksDeltaFactor;
        result.wait_time_s = result.wait_time_s_raw * ticksDeltaFactor;

        return result;
    }

    public override string ToString()
    {
        return $"waiting_tasks_count:{waiting_tasks_count,6:N} [{waiting_tasks_count_raw,6:N}]   wait_time_s:{wait_time_s,7:N3} [{wait_time_s_raw,7:N}]   max_wait_time_ms:{max_wait_time_ms,6:N}   signal_wait_time_s:{signal_wait_time_s,6:N} [{signal_wait_time_s_raw,6:N}]   wait_per_task_ms:{wait_per_task_ms,6:N}   ticks:{1000 * ticks / Stopwatch.Frequency,6:N0}ms";
    }

}