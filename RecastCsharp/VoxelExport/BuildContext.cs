using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Debug = UnityEngine.Debug;

namespace RecastSharp
{
    public static class RcFrequency
    {
        public static readonly double Frequency = (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency;
        public static long Ticks => unchecked((long)(Stopwatch.GetTimestamp() * Frequency));
    }

    public class BuildContext : Recast.rcContext
    {
        private readonly ThreadLocal<Dictionary<int, RcAtomicLong>> timerStart =
            new(() => new Dictionary<int, RcAtomicLong>());

        private readonly ConcurrentDictionary<int, RcAtomicLong> timerAccum = new();

        protected override void doStartTimer(Recast.rcTimerLabel label)
        {
            timerStart.Value[(int)label] = new RcAtomicLong(RcFrequency.Ticks);
        }

        protected override void doStopTimer(Recast.rcTimerLabel label)
        {
            timerAccum
                .GetOrAdd((int)label, _ => new RcAtomicLong(0))
                .AddAndGet(RcFrequency.Ticks - timerStart.Value?[(int)label].Read() ?? 0);
        }

        protected override void doResetTimers()
        {
            foreach (KeyValuePair<int, RcAtomicLong> keyValuePair in timerAccum)
            {
                keyValuePair.Value.Exchange(-1);
            }

            foreach (KeyValuePair<int, RcAtomicLong> keyValuePair in timerStart.Value)
            {
                keyValuePair.Value.Exchange(-1);
            }
        }

        protected override long doGetAccumulatedTime(Recast.rcTimerLabel label)
        {
            if (timerAccum.TryGetValue((int)label, out var timer))
            {
                return timer.Read();
            }

            return -1;
        }

        protected override void doLog(Recast.rcLogCategory category, string msg)
        {
            switch (category)
            {
                case Recast.rcLogCategory.RC_LOG_PROGRESS:
                    Debug.Log(msg);
                    break;
                case Recast.rcLogCategory.RC_LOG_WARNING:
                    Debug.LogWarning(msg);
                    break;
                case Recast.rcLogCategory.RC_LOG_ERROR:
                    Debug.LogError(msg);
                    break;
            }
        }
    }
}