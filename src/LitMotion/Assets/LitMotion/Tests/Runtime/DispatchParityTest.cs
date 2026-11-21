using System;
using System.Collections.Generic;
using System.Linq;
using LitMotion.Adapters;
using NUnit.Framework;

namespace LitMotion.Tests.Runtime
{
    // A motion that uses a fork feature goes through the same dispatch as any other motion (MotionStorage.Dispatch).
    // Apart from the events of the feature itself, its values, OnLoopComplete, OnComplete, OnCancel, the destination of
    // exceptions and its removal must be exactly those of the same motion without the feature. Each scenario is run
    // through the three entries (runner update, Time, Complete()) with and without an empty OnStart, and compared.
    public class DispatchParityTest
    {
        public enum Entry
        {
            Runner,
            SetTime,
            Complete,
        }

        public sealed class Scenario
        {
            public string Name;
            public Func<MotionBuilder<float, NoOptions, FloatMotionAdapter>, MotionBuilder<float, NoOptions, FloatMotionAdapter>> Configure;
            public bool BindThrows;
            public bool CancelThrows;
            public bool LoopThrows;
            public bool CompleteThrows;
            public bool HandlerThrows;
            public bool Preserve;
            public double Time = 1.25;

            public override string ToString() => Name;
        }

        static readonly Scenario[] Scenarios =
        {
            new() { Name = "plain", Configure = b => b },
            new() { Name = "plain, middle", Configure = b => b, Time = 0.5 },
            new() { Name = "loops", Configure = b => b.WithLoops(3), Time = 2.5 },
            new() { Name = "loops, yoyo, end", Configure = b => b.WithLoops(2, LoopType.Yoyo), Time = 2.5 },
            new() { Name = "every-loop delay, ends in the delay", Configure = b => b.WithLoops(3).WithDelay(0.5f, DelayType.EveryLoop), Time = 1.75 },
            new() { Name = "every-loop delay, values during delay", Configure = b => b.WithLoops(3).WithDelay(0.5f, DelayType.EveryLoop, false), Time = 1.75 },
            new() { Name = "first delay, not reached", Configure = b => b.WithDelay(2f), Time = 1.0 },
            new() { Name = "zero duration", Configure = b => b.WithLoops(2).WithDelay(0.5f), Time = 1.0 },
            new() { Name = "preserve", Configure = b => b, Preserve = true },
            new() { Name = "bind throws", Configure = b => b, BindThrows = true },
            new() { Name = "bind throws, cancel on error", Configure = b => b.WithCancelOnError(), BindThrows = true },
            new() { Name = "bind throws, cancel on error, middle", Configure = b => b.WithCancelOnError(), BindThrows = true, Time = 0.5 },
            new() { Name = "bind throws, cancel on error, OnCancel throws", Configure = b => b.WithCancelOnError(), BindThrows = true, CancelThrows = true },
            new() { Name = "bind throws, cancel on error, loops", Configure = b => b.WithCancelOnError().WithLoops(3), BindThrows = true, Time = 1.5 },
            new() { Name = "OnLoopComplete throws", Configure = b => b.WithLoops(3), LoopThrows = true, Time = 1.5 },
            new() { Name = "OnComplete throws", Configure = b => b, CompleteThrows = true },
            new() { Name = "handler throws", Configure = b => b, BindThrows = true, HandlerThrows = true },
            new() { Name = "handler throws, cancel on error", Configure = b => b.WithCancelOnError(), BindThrows = true, HandlerThrows = true },
        };

        static IEnumerable<TestCaseData> Cases()
        {
            foreach (var scenario in Scenarios)
            foreach (Entry entry in Enum.GetValues(typeof(Entry)))
            {
                yield return new TestCaseData(entry, scenario).SetName($"Test_SameEvents({entry}, {scenario.Name})");
            }
        }

        [TestCaseSource(nameof(Cases))]
        public void Test_SameEvents(Entry entry, Scenario scenario)
        {
            var without = Run(entry, scenario, false);
            var with = Run(entry, scenario, true);

            TestContext.WriteLine("without OnStart: " + string.Join(", ", without));
            TestContext.WriteLine("with OnStart:    " + string.Join(", ", with));
            Assert.That(with.Where(e => e != "start"), Is.EqualTo(without));
        }

        static List<string> Run(Entry entry, Scenario scenario, bool withOnStart)
        {
            var events = new List<string>();
            var dispatcher = new ManualMotionDispatcher();
            var saved = MotionDispatcher.GetUnhandledExceptionHandler();
            MotionDispatcher.RegisterUnhandledExceptionHandler(ex =>
            {
                events.Add("handler:" + ex.Message);
                if (scenario.HandlerThrows) throw new InvalidOperationException("from handler");
            });

            try
            {
                var builder = scenario.Configure(LMotion.Create(0f, 1f, scenario.Name.StartsWith("zero") ? 0f : 1f)
                        .WithScheduler(dispatcher.Scheduler)
                        .WithImmediateBind(false))
                    .WithOnLoopComplete(x =>
                    {
                        events.Add("loop:" + x);
                        if (scenario.LoopThrows) throw new ArgumentException("loop");
                    })
                    .WithOnComplete(() =>
                    {
                        events.Add("complete");
                        if (scenario.CompleteThrows) throw new ArgumentException("complete");
                    })
                    .WithOnCancel(() =>
                    {
                        events.Add("cancel");
                        if (scenario.CancelThrows) throw new ArgumentException("cancel");
                    });
                if (withOnStart) builder = builder.WithOnStart(() => events.Add("start"));

                var handle = builder.Bind(x =>
                {
                    events.Add("value:" + x);
                    if (scenario.BindThrows) throw new ArgumentException("bind");
                });
                if (scenario.Preserve) handle.Preserve();

                Capture(events, () =>
                {
                    switch (entry)
                    {
                        case Entry.Runner:
                            dispatcher.Update(scenario.Time);
                            break;
                        case Entry.SetTime:
                            handle.Time = scenario.Time;
                            break;
                        case Entry.Complete:
                            handle.Complete();
                            break;
                    }
                });
                events.Add($"after: active={handle.IsActive()} time={(handle.IsValid() ? handle.Time : double.NaN)}");

                for (int i = 0; i < 2; i++)
                {
                    Capture(events, () => dispatcher.Update(0.25));
                    events.Add($"update {i}: valid={handle.IsValid()} active={handle.IsActive()}");
                }
            }
            finally
            {
                MotionDispatcher.RegisterUnhandledExceptionHandler(saved);
                dispatcher.Reset();
            }

            return events;
        }

        static void Capture(List<string> events, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                events.Add("threw:" + ex.Message);
            }
        }
    }
}
