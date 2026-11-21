using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.TestTools.Utils;
using Unity.Jobs;
using UnityEngine.TestTools.Constraints;
using AllocIs = UnityEngine.TestTools.Constraints.Is;
using Is = NUnit.Framework.Is;

[assembly: RegisterGenericJobType(typeof(LitMotion.MotionUpdateJob<float, LitMotion.NoOptions, LitMotion.Tests.Runtime.PlaybackControlTest.EchoTimeAdapter>))]

namespace LitMotion.Tests.Runtime
{
    public class PlaybackControlTest
    {
        ManualMotionDispatcher dispatcher;
        float value;

        [SetUp]
        public void SetUp()
        {
            dispatcher = new ManualMotionDispatcher();
            value = -1f;
        }

        MotionBuilder<float, NoOptions, Adapters.FloatMotionAdapter> Create(float duration)
        {
            return LMotion.Create(0f, 10f, duration).WithScheduler(dispatcher.Scheduler);
        }

        MotionHandle Bind(MotionBuilder<float, NoOptions, Adapters.FloatMotionAdapter> builder)
        {
            return builder.Bind(this, (x, test) => test.value = x);
        }

        static void AssertValue(float actual, float expected)
        {
            Assert.That(actual, Is.EqualTo(expected).Using(new FloatEqualityComparer(0.0001f)));
        }

        [Test]
        public void Test_PauseAndPlay_RestoresPreviousSpeed()
        {
            var handle = Bind(Create(1f));
            handle.PlaybackSpeed = 2f;

            dispatcher.Update(0.25);
            AssertValue(value, 5f);

            handle.Pause();
            Assert.That(handle.IsPaused(), Is.True);
            Assert.That(handle.PlaybackSpeed, Is.EqualTo(0f));
            dispatcher.Update(0.25);
            AssertValue(value, 5f);

            handle.Pause(); // must not overwrite the remembered speed
            handle.Play();
            Assert.That(handle.IsPaused(), Is.False);
            Assert.That(handle.PlaybackSpeed, Is.EqualTo(2f));
            dispatcher.Update(0.125);
            AssertValue(value, 7.5f);

            handle.Play(); // no-op when not paused
            Assert.That(handle.PlaybackSpeed, Is.EqualTo(2f));
        }

        [Test]
        public void Test_PauseAndPlay_KeepsNegativeSpeed()
        {
            var handle = Bind(Create(1f)).Preserve();
            dispatcher.Update(0.5);
            handle.PlaybackSpeed = -0.5f;
            handle.Pause();
            handle.Play();
            Assert.That(handle.PlaybackSpeed, Is.EqualTo(-0.5f));
            handle.Cancel();
        }

        [Test]
        public void Test_SpeedChangedWhilePaused_EndsPause()
        {
            var handle = Bind(Create(1f));
            handle.Pause();
            handle.PlaybackSpeed = 3f;
            Assert.That(handle.IsPaused(), Is.False);
            handle.Play();
            Assert.That(handle.PlaybackSpeed, Is.EqualTo(3f));
            handle.Cancel();
        }

        [Test]
        public void Test_TogglePause()
        {
            var handle = Bind(Create(1f));
            handle.PlaybackSpeed = 1.5f;
            handle.TogglePause();
            Assert.That(handle.IsPaused(), Is.True);
            handle.TogglePause();
            Assert.That(handle.IsPaused(), Is.False);
            Assert.That(handle.PlaybackSpeed, Is.EqualTo(1.5f));
            handle.Cancel();
        }

        [Test]
        public void Test_PauseDuringDelay_PausesTheDelay()
        {
            var handle = Bind(Create(1f).WithDelay(0.5f));
            handle.Pause();
            dispatcher.Update(1.0);
            Assert.That(handle.Time, Is.EqualTo(0.0));
            handle.Play();
            dispatcher.Update(0.75);
            AssertValue(value, 2.5f);
        }

        [Test]
        public void Test_Restart()
        {
            var starts = 0;
            var completes = 0;
            var handle = Bind(Create(1f)
                .WithOnStart(() => starts++)
                .WithOnComplete(() => completes++))
                .Preserve();

            dispatcher.Update(1.5);
            Assert.That(completes, Is.EqualTo(1));
            AssertValue(value, 10f);

            handle.PlaybackSpeed = -2f;
            handle.Pause();
            handle.Restart();
            Assert.That(handle.Time, Is.EqualTo(0.0));
            Assert.That(handle.IsPaused(), Is.False);
            Assert.That(handle.PlaybackSpeed, Is.EqualTo(2f));
            AssertValue(value, 0f);

            dispatcher.Update(0.25);
            AssertValue(value, 5f);
            dispatcher.Update(0.5);
            Assert.That(completes, Is.EqualTo(2));
            Assert.That(starts, Is.EqualTo(1));
            handle.Cancel();
        }

        [Test]
        public void Test_PlayBackwards_PausesAtStart()
        {
            var completes = 0;
            var handle = Bind(Create(1f).WithOnComplete(() => completes++)).Preserve();
            dispatcher.Update(0.5);
            AssertValue(value, 5f);

            handle.PlayBackwards();
            Assert.That(handle.PlaybackSpeed, Is.EqualTo(-1f));
            dispatcher.Update(0.25);
            AssertValue(value, 2.5f);

            dispatcher.Update(1.0);
            AssertValue(value, 0f);
            Assert.That(handle.Time, Is.EqualTo(0.0));
            Assert.That(handle.IsPaused(), Is.True);
            Assert.That(handle.IsActive(), Is.True);
            Assert.That(completes, Is.EqualTo(0));

            dispatcher.Update(1.0);
            Assert.That(handle.Time, Is.EqualTo(0.0));

            handle.PlayForward();
            Assert.That(handle.PlaybackSpeed, Is.EqualTo(1f));
            dispatcher.Update(0.25);
            AssertValue(value, 2.5f);
            dispatcher.Update(1.0);
            Assert.That(completes, Is.EqualTo(1));
            handle.Cancel();
        }

        [Test]
        public void Test_PlayBackwards_WithDelay_BindsTheStartValue()
        {
            // skipValuesDuringDelay is true by default: without the stop at the start the last value would remain.
            var handle = Bind(Create(1f).WithDelay(0.5f)).Preserve();
            dispatcher.Update(1.0);
            AssertValue(value, 5f);

            handle.PlayBackwards();
            dispatcher.Update(0.75);
            AssertValue(value, 0f);
            Assert.That(handle.Time, Is.EqualTo(0.5));
            Assert.That(handle.IsPaused(), Is.True);

            // The delay is not waited again.
            handle.PlayForward();
            dispatcher.Update(0.25);
            AssertValue(value, 2.5f);
            handle.Cancel();
        }

        [Test]
        public void Test_PlayBackwards_FromCompletedMotion_StartsFromTheEnd()
        {
            var handle = Bind(Create(1f)).Preserve();
            dispatcher.Update(1.0);
            dispatcher.Update(5.0); // the time of a preserved motion keeps advancing after completion
            AssertValue(value, 10f);

            handle.PlayBackwards();
            Assert.That(handle.Time, Is.EqualTo(1.0));
            dispatcher.Update(0.25);
            AssertValue(value, 7.5f);
            handle.Cancel();
        }

        [Test]
        public void Test_PlayBackwards_DuringInitialDelay_PausesAtStart()
        {
            var starts = 0;
            var handle = Bind(Create(1f).WithDelay(1f).WithOnStart(() => starts++));
            dispatcher.Update(0.5);

            handle.PlayBackwards();
            Assert.That(handle.Time, Is.EqualTo(1.0));
            Assert.That(handle.IsPaused(), Is.True);
            AssertValue(value, 0f);
            dispatcher.Update(0.5);
            Assert.That(starts, Is.EqualTo(0), "OnStart is deferred while paused");

            handle.PlayForward();
            dispatcher.Update(0.25);
            Assert.That(starts, Is.EqualTo(1));
            AssertValue(value, 2.5f);
            handle.Cancel();
        }

        [Test]
        public void Test_PlayBackwards_WithLoops()
        {
            var handle = Bind(Create(1f).WithLoops(3)).Preserve();
            dispatcher.Update(2.5);
            AssertValue(value, 5f);

            handle.PlayBackwards();
            dispatcher.Update(1.0); // back into the second loop
            AssertValue(value, 5f);
            dispatcher.Update(2.0);
            AssertValue(value, 0f);
            Assert.That(handle.Time, Is.EqualTo(0.0));
            Assert.That(handle.IsPaused(), Is.True);
            handle.Cancel();
        }

        [TestCase(0f)]
        [TestCase(1f)]
        public void Test_PlayBackwards_PausesInTheUpdateThatLandsExactlyOnTheStart(float delay)
        {
            var handle = Bind(Create(1f).WithDelay(delay));
            handle.Time = delay + 0.5;
            handle.PlayBackwards();

            dispatcher.Update(0.5);

            Assert.That(handle.Time, Is.EqualTo((double)delay));
            Assert.That(handle.IsPaused(), Is.True);
            Assert.That(handle.PlaybackSpeed, Is.EqualTo(0f));
            AssertValue(value, 0f);
            handle.Cancel();
        }

        public readonly struct EchoTimeAdapter : IMotionAdapter<float, NoOptions>
        {
            public float Evaluate(ref float startValue, ref float endValue, ref NoOptions options, in MotionEvaluationContext context)
            {
                return (float)context.Time;
            }
        }

        [Test]
        public void Test_PlayBackwards_TheValueIsEvaluatedAtTheStart()
        {
            // An adapter that depends on the time (not only on the progress) must receive the clamped time.
            var echoed = -1f;
            var handle = LMotion.Create<float, NoOptions, EchoTimeAdapter>(0f, 1f, 2f)
                .WithScheduler(dispatcher.Scheduler)
                .WithDelay(1f)
                .Bind(x => echoed = x);
            handle.Time = 2;
            handle.PlayBackwards();

            dispatcher.Update(1.25);

            Assert.That(handle.Time, Is.EqualTo(1.0));
            Assert.That(handle.IsPaused(), Is.True);
            Assert.That(echoed, Is.EqualTo(1f));
            handle.Cancel();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Test_PlayBackwards_ZeroDurationWithEveryLoopDelay_StaysAliveAtTheStart(bool preserve)
        {
            var completed = 0;
            var handle = LMotion.Create(0f, 10f, 0f).WithScheduler(dispatcher.Scheduler)
                .WithDelay(1f, DelayType.EveryLoop)
                .WithOnComplete(() => completed++)
                .RunWithoutBinding();
            if (preserve) handle.Preserve();

            handle.PlayBackwards();
            dispatcher.Update(0.5);

            Assert.That(completed, Is.Zero);
            Assert.That(handle.IsActive(), Is.True);
            Assert.That(handle.IsPaused(), Is.True);
            Assert.That(handle.Time, Is.EqualTo(0.0));

            handle.PlayForward();
            dispatcher.Update(1.5);
            Assert.That(completed, Is.EqualTo(1));
        }

        // A completed zero-duration motion with a delay before every loop goes back to Time 0, which is in its delay:
        // the start value is bound even though values are skipped during the delay.
        [Test]
        public void Test_PlayBackwards_ZeroDurationWithEveryLoopDelay_FromCompleted_BindsTheStartValue()
        {
            var value = -1f;
            var completed = 0;
            var handle = Create(0f)
                .WithDelay(1f, DelayType.EveryLoop)
                .WithOnComplete(() => completed++)
                .Bind(x => value = x)
                .Preserve();
            handle.Complete();
            Assert.That(value, Is.EqualTo(10f));

            handle.PlayBackwards();
            dispatcher.Update(1.0);
            dispatcher.Update(0.125);

            Assert.That(handle.Time, Is.EqualTo(0.0));
            Assert.That(handle.IsPaused(), Is.True);
            Assert.That(handle.IsActive(), Is.True);
            Assert.That(completed, Is.EqualTo(1));
            Assert.That(value, Is.EqualTo(0f));
            handle.Cancel();
        }

        // Already at its start: PlayBackwards() stops it there immediately and binds the start value, like the stop of the runner.
        [Test]
        public void Test_PlayBackwards_ZeroDurationWithEveryLoopDelay_AtTheStart_BindsTheStartValueImmediately()
        {
            var writes = new List<float>();
            var completed = 0;
            var handle = Create(0f)
                .WithImmediateBind(false)
                .WithDelay(1f, DelayType.EveryLoop, true)
                .WithOnComplete(() => completed++)
                .Bind(writes.Add);

            handle.PlayBackwards();
            Assert.That(writes, Is.EqualTo(new[] { 0f }));

            dispatcher.Update(0.25);
            Assert.That(handle.Time, Is.EqualTo(0.0));
            Assert.That(handle.IsPaused(), Is.True);
            Assert.That(handle.IsActive(), Is.True);
            Assert.That(completed, Is.Zero);
            Assert.That(writes, Is.EqualTo(new[] { 0f }));
            handle.Cancel();
        }

        // Goto the end of a zero-duration motion completes it, like any other motion (its Time alone does not:
        // a zero-duration motion completes when its time goes past the end of the delay).
        [TestCase(1f)]
        [TestCase(0f)]
        public void Test_Goto_EndOfZeroDurationMotion_StartsAndCompletesIt(float delay)
        {
            var events = new List<string>();
            var handle = Create(0f)
                .WithDelay(delay)
                .WithOnStart(() => events.Add("start"))
                .WithOnComplete(() => events.Add("complete"))
                .Bind(x => events.Add("value=" + x));
            events.Clear(); // value bound by Bind()

            handle.Goto(handle.TotalDuration);

            Assert.That(events, Is.EqualTo(new[] { "start", "value=10", "complete" }));
            Assert.That(handle.IsActive(), Is.False);
        }

        [Test]
        public void Test_AssigningPlaybackSpeed_EndsTheBackwardsMode()
        {
            var handle = Bind(Create(2f).WithDelay(1f));
            handle.Time = 1.5;
            handle.PlayBackwards();
            handle.PlaybackSpeed = 1f;
            dispatcher.Update(0.25);
            handle.PlaybackSpeed = -1f;

            dispatcher.Update(1.0);

            // A negative speed set directly plays in reverse like in the original library (no stop at the start).
            Assert.That(handle.Time, Is.EqualTo(0.75));
            Assert.That(handle.IsPaused(), Is.False);
            handle.Cancel();
        }

        [Test]
        public void Test_AssigningPlaybackSpeed_EndsThePause()
        {
            var handle = Bind(Create(1f));
            handle.Pause();
            handle.PlaybackSpeed = 2f;
            dispatcher.Update(0.125);
            Assert.That(handle.IsPaused(), Is.False);

            handle.PlaybackSpeed = 0f;
            Assert.That(handle.IsPaused(), Is.False, "The pause of Pause() must not come back");
            handle.Play();
            Assert.That(handle.PlaybackSpeed, Is.EqualTo(0f));
            handle.Cancel();
        }

        [Test]
        public void Test_Goto_InvalidArguments()
        {
            var handle = Bind(Create(1f));
            Assert.Throws<ArgumentOutOfRangeException>(() => handle.Goto(double.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => handle.TryGoto(double.NaN));
            handle.Goto(double.PositiveInfinity);
            Assert.That(handle.IsActive(), Is.False, "A finite motion goes to its end");

            var infinite = Bind(Create(1f).WithLoops(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => infinite.Goto(double.PositiveInfinity));
            infinite.Goto(double.NegativeInfinity);
            Assert.That(infinite.Time, Is.EqualTo(0.0));
            infinite.Cancel();
        }

        [Test]
        public void Test_Goto()
        {
            var handle = Bind(Create(1f)).Preserve();

            handle.Goto(0.5);
            AssertValue(value, 5f);
            Assert.That(handle.IsPaused(), Is.True);
            dispatcher.Update(1.0);
            AssertValue(value, 5f);

            handle.Goto(0.25, andPlay: true);
            AssertValue(value, 2.5f);
            Assert.That(handle.IsPaused(), Is.False);
            dispatcher.Update(0.25);
            AssertValue(value, 5f);

            handle.Goto(-1.0);
            Assert.That(handle.Time, Is.EqualTo(0.0));
            handle.Goto(100.0);
            Assert.That(handle.Time, Is.EqualTo(1.0));
            AssertValue(value, 10f);

            Assert.Throws<ArgumentOutOfRangeException>(() => handle.Goto(double.NaN));
            handle.Cancel();
        }

        [Test]
        public void Test_InvalidHandles()
        {
            var canceled = Bind(Create(1f));
            canceled.Cancel();
            AssertInvalid(canceled);

            var removed = Bind(Create(0.1f));
            dispatcher.Update(0.2);
            dispatcher.Update(0.1);
            AssertInvalid(removed);

            AssertInvalid(default);

            var child = Bind(Create(1f));
            var sequence = LSequence.Create().Append(child).Run(b => b.WithScheduler(dispatcher.Scheduler));
            AssertInvalid(child);
            sequence.Cancel();
        }

        static void AssertInvalid(MotionHandle handle)
        {
            Assert.Throws<InvalidOperationException>(() => handle.Pause());
            Assert.Throws<InvalidOperationException>(() => handle.Play());
            Assert.Throws<InvalidOperationException>(() => handle.TogglePause());
            Assert.Throws<InvalidOperationException>(() => handle.PlayForward());
            Assert.Throws<InvalidOperationException>(() => handle.PlayBackwards());
            Assert.Throws<InvalidOperationException>(() => handle.Restart());
            Assert.Throws<InvalidOperationException>(() => handle.Goto(0));

            Assert.That(handle.TryPause(), Is.False);
            Assert.That(handle.TryPlay(), Is.False);
            Assert.That(handle.TryTogglePause(), Is.False);
            Assert.That(handle.TryPlayForward(), Is.False);
            Assert.That(handle.TryPlayBackwards(), Is.False);
            Assert.That(handle.TryRestart(), Is.False);
            Assert.That(handle.TryGoto(0), Is.False);
            Assert.That(handle.IsPaused(), Is.False);
        }

        // A handle built by hand with an index out of the range of the slots is an invalid handle like any other.
        [TestCase(-1)]
        [TestCase(int.MaxValue)]
        public void Test_HandleWithAnIndexOutOfRange_IsInvalid(int index)
        {
            var handle = Create(1f).RunWithoutBinding();
            handle.Index = index;

            Assert.That(handle.IsPaused(), Is.False);
            Assert.That(handle.TryPause(), Is.False);
            Assert.That(handle.TryPlay(), Is.False);
            Assert.That(handle.TryGoto(0.5), Is.False);
            Assert.Throws<InvalidOperationException>(() => handle.Pause());
            Assert.Throws<InvalidOperationException>(() => handle.PlayBackwards());
            Assert.Throws<InvalidOperationException>(() => handle.SetTarget(new object()));
        }

        [Test]
        public void Test_TryMethods_ReturnTrueForActiveMotion()
        {
            var handle = Bind(Create(1f)).Preserve();
            Assert.That(handle.TryPause(), Is.True);
            Assert.That(handle.TryPlay(), Is.True);
            Assert.That(handle.TryTogglePause(), Is.True);
            Assert.That(handle.TryPlayBackwards(), Is.True);
            Assert.That(handle.TryPlayForward(), Is.True);
            Assert.That(handle.TryRestart(), Is.True);
            Assert.That(handle.TryGoto(0.5), Is.True);
            handle.Cancel();
        }

        [Test]
        public void Test_Sequence()
        {
            var callbacks = 0;
            var child = Bind(Create(1f));
            var handle = LSequence.Create()
                .Append(child)
                .InsertCallback(0.5f, () => callbacks++)
                .Run(b => b.WithScheduler(dispatcher.Scheduler))
                .Preserve();

            dispatcher.Update(0.75);
            AssertValue(value, 7.5f);
            Assert.That(callbacks, Is.EqualTo(1));

            handle.Pause();
            dispatcher.Update(0.5);
            AssertValue(value, 7.5f);

            handle.PlayBackwards();
            dispatcher.Update(2.0);
            AssertValue(value, 0f);
            Assert.That(handle.IsPaused(), Is.True);
            Assert.That(callbacks, Is.EqualTo(1));

            handle.PlayForward();
            dispatcher.Update(0.75);
            AssertValue(value, 7.5f);
            Assert.That(callbacks, Is.EqualTo(2));

            handle.Restart();
            AssertValue(value, 0f);
            dispatcher.Update(0.75);
            Assert.That(callbacks, Is.EqualTo(3));
            handle.Cancel();
        }

        [UnityTest]
        public IEnumerator Test_PlayerLoop_PlayBackwards()
        {
            var v = -1f;
            var handle = LMotion.Create(0f, 10f, 1f).Bind(x => v = x).Preserve();
            yield return new WaitForSeconds(0.2f);

            // Make sure the motion is reversed from the middle, not from its completed state.
            Assert.That(handle.IsPlaying(), Is.True);
            Assert.That(handle.Time, Is.GreaterThan(0.0).And.LessThan(1.0));
            var reversedFrom = handle.Time;

            handle.PlayBackwards();
            yield return null;
            Assert.That(handle.Time, Is.LessThan(reversedFrom));

            yield return new WaitForSeconds((float)reversedFrom + 0.3f);
            Assert.That(v, Is.EqualTo(0f));
            Assert.That(handle.IsPaused(), Is.True);
            Assert.That(handle.Time, Is.EqualTo(0.0));
            handle.Cancel();
        }

        [Test]
        public void Test_NoAllocation()
        {
            var handle = Bind(Create(1f)).Preserve();
            dispatcher.Update(0.5);

            TestDelegate verbs = () =>
            {
                handle.Pause();
                handle.IsPaused();
                handle.Play();
                handle.TogglePause();
                handle.TogglePause();
                handle.PlayBackwards();
                handle.PlayForward();
                handle.Restart();
                handle.Goto(0.5);
                handle.TryGoto(0.25, true);
                handle.TryPause();
                handle.TryPlay();
                handle.TryPlayBackwards();
                handle.TryPlayForward();
                handle.TryRestart();
            };

            verbs(); // warm up
            Assert.That(verbs, AllocIs.Not.AllocatingGCMemory());
            handle.Cancel();
        }

        [Test]
        public void Test_NoAdditionalAllocation_WhenStoppingAtStart()
        {
            var plainDispatcher = new ManualMotionDispatcher();
            var handle = Bind(Create(1f)).Preserve();
            var plain = LMotion.Create(0f, 10f, 1f).WithScheduler(plainDispatcher.Scheduler).RunWithoutBinding().Preserve();
            dispatcher.Update(0.5);
            plainDispatcher.Update(0.5);

            handle.PlayBackwards();
            plain.PlaybackSpeed = -1f;
            var withBackwards = GCAllocationCounter.Count(() =>
            {
                for (int i = 0; i < 4; i++) dispatcher.Update(0.25);
            });
            var withoutBackwards = GCAllocationCounter.Count(() =>
            {
                for (int i = 0; i < 4; i++) plainDispatcher.Update(0.25);
            });

            Assert.That(handle.IsPaused(), Is.True);
            Assert.That(withBackwards, Is.EqualTo(withoutBackwards));
            handle.Cancel();
            plain.Cancel();
        }
    }
}
