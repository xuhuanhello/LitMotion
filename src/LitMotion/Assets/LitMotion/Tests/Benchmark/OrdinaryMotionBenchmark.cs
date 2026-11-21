using System;
using System.Diagnostics;
using System.Globalization;
using LitMotion.Adapters;
using NUnit.Framework;
using Unity.Burst;
using Unity.PerformanceTesting;

namespace LitMotion.Tests.Benchmark
{
    // Update of ordinary motions (no fork feature) and PlaybackSpeed assignment, at several numbers of motions and storage
    // capacities. Explicit: it does not run by default; select it by name (see README.md, 6.1).
    public sealed class OrdinaryMotionBenchmark
    {
        static readonly Action<float> Binding = _ => { };
        static readonly (int Active, int Capacity)[] Cases =
        {
            (50, 64), (64, 64), (64, 128),
            (500, 512), (512, 512), (512, 1024),
            (5000, 8192), (8192, 8192), (8192, 16384),
            (10000, 16384), (16384, 16384)
        };

        [Test]
        [Performance]
        [Explicit]
        public void Benchmark_PlaybackSpeedAndUpdate()
        {
            const int trials = 11;
            var solo = new ManualMotionDispatcher();
            var handle = LMotion.Create(0f, 1f, 100f).WithScheduler(solo.Scheduler).RunWithoutBinding();
            var dispatchers = new ManualMotionDispatcher[Cases.Length];
            var samples = new double[Cases.Length][];
            var setter = new double[trials];
            for (int i = 0; i < Cases.Length; i++)
            {
                var (active, capacity) = Cases[i];
                dispatchers[i] = MakeDispatcher(active, capacity);
                samples[i] = new double[trials];
            }

            try
            {
                Assert.That(BurstCompiler.IsEnabled, Is.True);
                WriteSpeeds(handle, 200_000);
                foreach (var dispatcher in dispatchers) Advance(dispatcher, 100);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                for (int trial = 0; trial < trials; trial++)
                {
                    long start = Stopwatch.GetTimestamp();
                    WriteSpeeds(handle, 1_000_000);
                    setter[trial] = ElapsedMilliseconds(start);

                    for (int i = 0; i < Cases.Length; i++)
                    {
                        int index = (i + trial) % Cases.Length;
                        int groups = Cases[index].Active <= 512 ? 10 : 1;
                        start = Stopwatch.GetTimestamp();
                        Advance(dispatchers[index], groups * 100);
                        samples[index][trial] = ElapsedMilliseconds(start) / groups;
                    }
                }

                Assert.That(handle.PlaybackSpeed, Is.EqualTo(2f));
                Print("setter", setter);
                for (int i = 0; i < Cases.Length; i++)
                {
                    Print($"{Cases[i].Active}/{Cases[i].Capacity}", samples[i]);
                }
            }
            finally
            {
                foreach (var dispatcher in dispatchers) dispatcher.Reset();
                solo.Reset();
            }
        }

        static ManualMotionDispatcher MakeDispatcher(int active, int capacity)
        {
            var dispatcher = new ManualMotionDispatcher();
            dispatcher.EnsureStorageCapacity<float, NoOptions, FloatMotionAdapter>(capacity);
            for (int i = 0; i < active; i++)
            {
                LMotion.Create(0f, 1f, 100f).WithScheduler(dispatcher.Scheduler).Bind(Binding);
            }
            return dispatcher;
        }

        static void WriteSpeeds(MotionHandle handle, int count)
        {
            for (int i = 0; i < count; i++) handle.PlaybackSpeed = 1 + (i & 1);
        }

        static void Advance(ManualMotionDispatcher dispatcher, int count)
        {
            for (int i = 0; i < count; i++) dispatcher.Update(0.001);
        }

        static double ElapsedMilliseconds(long start)
        {
            return 1000.0 * (Stopwatch.GetTimestamp() - start) / Stopwatch.Frequency;
        }

        static void Print(string label, double[] values)
        {
            var group = new SampleGroup(label, SampleUnit.Millisecond);
            foreach (var value in values) Measure.Custom(group, value);

            var sorted = (double[])values.Clone();
            Array.Sort(sorted);
            var samples = Array.ConvertAll(values, value => value.ToString("F3", CultureInfo.InvariantCulture));
            TestContext.WriteLine(FormattableString.Invariant($"{label}: median={sorted[sorted.Length / 2]:F3} ms; samples={string.Join(",", samples)}"));
        }
    }
}
