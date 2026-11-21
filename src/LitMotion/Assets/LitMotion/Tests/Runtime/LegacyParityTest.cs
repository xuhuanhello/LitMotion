using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Burst;

namespace LitMotion.Tests.Runtime
{
    // Code that only uses the API of LitMotion v2.0.2 must behave exactly as in v2.0.2, including its quirks.
    // These tests pin that behavior down; they pass unchanged on the unmodified v2.0.2 package.
    // (The one intended difference, motions created during a runner update, is covered by ReentrancyTest.)
    public class LegacyParityTest
    {
        ManualMotionDispatcher dispatcher;

        [SetUp]
        public void SetUp()
        {
            dispatcher = new ManualMotionDispatcher();
        }

        [TearDown]
        public void TearDown()
        {
            dispatcher.Reset();
        }

        MotionBuilder<float, NoOptions, Adapters.FloatMotionAdapter> Create(float duration = 1f)
        {
            return LMotion.Create(0f, 1f, duration).WithScheduler(dispatcher.Scheduler).WithImmediateBind(false);
        }

        [Test]
        public void Test_SequenceSeekInsideChildBind_TheSecondChildIsWrittenTwice()
        {
            var secondValues = new List<float>();
            var sequence = default(MotionHandle);
            var first = Create().Bind(x =>
            {
                if (x > 0.5f) sequence.Time = 0.25;
            });
            var second = Create().Bind(secondValues.Add);
            sequence = LSequence.Create()
                .Append(first)
                .Insert(0.125f, second)
                .Run(b => b.WithScheduler(dispatcher.Scheduler));

            sequence.Time = 0.75;

            Assert.That(sequence.Time, Is.EqualTo(0.25));
            Assert.That(secondValues, Is.EqualTo(new[] { 0.125f, 0.125f }));
        }

        [Test]
        public void Test_BindSeeksItsOwnMotion_BothValuesAreWritten()
        {
            var values = new List<float>();
            var handle = default(MotionHandle);
            handle = Create().Bind(x =>
            {
                values.Add(x);
                if (values.Count == 1) handle.Time = 0.75;
            });

            dispatcher.Update(0.25);

            Assert.That(values, Is.EqualTo(new[] { 0.25f, 0.75f }));
            Assert.That(handle.Time, Is.EqualTo(0.75));
        }

        [Test]
        public void Test_SequenceCompletes_ChildGetsOnCompleteThenOnCancel()
        {
            var events = new List<string>();
            LSequence.Create()
                .Append(Create(0.5f)
                    .WithOnComplete(() => events.Add("complete"))
                    .WithOnCancel(() => events.Add("cancel"))
                    .RunWithoutBinding())
                .Run(b => b.WithScheduler(dispatcher.Scheduler));

            dispatcher.Update(1.0);

            Assert.That(events, Is.EqualTo(new[] { "complete", "cancel" }));
        }

        [Test]
        public void Test_SequenceLoop_ChildIsOnlySampledAtTheNewPosition()
        {
            var values = new List<float>();
            var completes = 0;
            LSequence.Create()
                .Append(Create().WithOnComplete(() => completes++).Bind(values.Add))
                .Run(b => b.WithScheduler(dispatcher.Scheduler).WithLoops(2));

            dispatcher.Update(0.75);
            dispatcher.Update(0.5);

            Assert.That(values, Is.EqualTo(new[] { 0.75f, 0.25f }));
            Assert.That(completes, Is.Zero);
        }

        [Test]
        public void Test_ZeroDurationChildAtTheEndOfASequence_NeverCompletes()
        {
            var completed = false;
            var canceled = false;
            LSequence.Create()
                .Append(Create(0.5f).RunWithoutBinding())
                .Append(Create(0f).WithOnComplete(() => completed = true).WithOnCancel(() => canceled = true).RunWithoutBinding())
                .Run(b => b.WithScheduler(dispatcher.Scheduler));

            dispatcher.Update(1.0);

            Assert.That(completed, Is.False);
            Assert.That(canceled, Is.True);
        }

        [Test]
        public void Test_NegativePlaybackSpeed_DoesNotStopAtTheStart()
        {
            var handle = Create().RunWithoutBinding();
            dispatcher.Update(0.5);
            handle.PlaybackSpeed = -1f;
            dispatcher.Update(1.0);

            Assert.That(handle.Time, Is.EqualTo(-0.5));
            Assert.That(handle.IsActive(), Is.True);
            handle.Cancel();
        }

        [Test]
        public void Test_ChildBindCancelsTheSequence_EarlierChildIsWrittenAgain()
        {
            // Quirk of v2.0.2: after the sequence is canceled from a child, the rest of the update still evaluates
            // the other children, which reactivates them.
            var parent = default(MotionHandle);
            var earlierWrites = 0;
            var canceled = false;
            var earlier = Create(0.5f).Bind(_ => earlierWrites++);
            var later = Create(0.5f).Bind(_ =>
            {
                if (canceled) return;
                canceled = true;
                parent.Cancel();
            });
            parent = LSequence.Create()
                .Insert(0.25f, earlier)
                .Insert(0.5f, later)
                .Run(b => b.WithScheduler(dispatcher.Scheduler));

            parent.Time = 0.5;

            Assert.That(parent.IsActive(), Is.False);
            Assert.That(earlierWrites, Is.EqualTo(1));
            Assert.That(earlier.IsActive(), Is.True);
        }

        [Test]
        public void Test_ChildResetBeforeRun_RunStillReturnsAnActiveSequence()
        {
            var child = Create().RunWithoutBinding();
            var builder = LSequence.Create().Append(child);
            dispatcher.Reset();

            var parent = default(MotionHandle);
            Assert.DoesNotThrow(() => parent = builder.Run(b => b.WithScheduler(dispatcher.Scheduler)));

            Assert.That(child.IsValid(), Is.False);
            Assert.That(parent.IsActive(), Is.True);
        }

        [Test]
        public void Test_SequenceSeekToAChildStart_ThatChildIsEvaluatedFirst()
        {
            // v2.0.2 evaluates the children that start at or after the new time first (from the last), then the others.
            var order = new List<string>();
            var first = Create(0.5f).Bind(_ => order.Add("first"));
            var second = Create(0.5f).Bind(_ => order.Add("second"));
            var sequence = LSequence.Create()
                .Append(first)
                .Append(second)
                .Run(b => b.WithScheduler(dispatcher.Scheduler));

            sequence.Time = 0.5;

            Assert.That(order, Is.EqualTo(new[] { "second", "first" }));
        }

        public struct CallbackAdapter : IMotionAdapter<float, NoOptions>
        {
            public static Action OnEvaluate;

            [BurstDiscard]
            static void InvokeManaged() => OnEvaluate?.Invoke();

            public float Evaluate(ref float startValue, ref float endValue, ref NoOptions options, in MotionEvaluationContext context)
            {
                InvokeManaged();
                return startValue + (endValue - startValue) * context.Progress;
            }
        }

        [Test]
        public void Test_AdapterCancelsThePreservedMotionDuringComplete_TheCompletionIsStillDispatched()
        {
            // v2.0.2: Complete() binds the end value and invokes OnLoopComplete/OnComplete whatever the adapter did.
            var events = new List<string>();
            var handle = default(MotionHandle);
            handle = LMotion.Create<float, NoOptions, CallbackAdapter>(0f, 1f, 1f)
                .WithScheduler(dispatcher.Scheduler).WithImmediateBind(false)
                .WithOnCancel(() => events.Add("cancel"))
                .WithOnLoopComplete(x => events.Add("loop:" + x))
                .WithOnComplete(() => events.Add("complete"))
                .Bind(x => events.Add("bind:" + x))
                .Preserve();
            try
            {
                CallbackAdapter.OnEvaluate = () => handle.Cancel();
                var completed = handle.TryComplete();

                Assert.That(completed, Is.True);
                Assert.That(events, Is.EqualTo(new[] { "cancel", "bind:1", "loop:1", "complete" }));
            }
            finally
            {
                CallbackAdapter.OnEvaluate = null;
            }
        }

        // v2.0.2 reads the managed entry of the motion after the evaluation: a continuation that the adapter registers after
        // growing the storage (which reallocates the arrays) is invoked by the completion.
        [TestCase("Complete", false)]
        [TestCase("Complete", true)]
        [TestCase("SetTime", false)]
        [TestCase("SetTime", true)]
        public void Test_AdapterGrowsTheStorageAndRegistersAContinuation_TheCompletionInvokesIt(string entry, bool growStorage)
        {
            var events = new List<string>();
            var handle = LMotion.Create<float, NoOptions, CallbackAdapter>(0f, 1f, 1f)
                .WithScheduler(dispatcher.Scheduler).WithImmediateBind(false)
                .WithOnComplete(() => events.Add("complete"))
                .Bind(_ => events.Add("bind"))
                .Preserve();
            try
            {
                CallbackAdapter.OnEvaluate = () =>
                {
                    CallbackAdapter.OnEvaluate = null;
                    if (growStorage) dispatcher.EnsureStorageCapacity<float, NoOptions, CallbackAdapter>(128);
                    handle.GetAwaiter().OnCompleted(() => events.Add("continuation"));
                };

                if (entry == "Complete") Assert.That(handle.TryComplete(), Is.True);
                else handle.Time = 1.25;

                Assert.That(events, Is.EqualTo(new[] { "bind", "complete", "continuation" }));
            }
            finally
            {
                CallbackAdapter.OnEvaluate = null;
            }
        }

        [Test]
        public void Test_PlaybackSpeedZero_StopsAndResumes()
        {
            var value = -1f;
            var handle = Create().Bind(x => value = x);
            handle.PlaybackSpeed = 0f;
            dispatcher.Update(0.5);
            Assert.That(value, Is.EqualTo(0f));
            handle.PlaybackSpeed = 1f;
            dispatcher.Update(0.5);
            Assert.That(value, Is.EqualTo(0.5f));
            handle.Cancel();
        }
    }
}
