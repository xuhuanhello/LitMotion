using System;
using System.Collections.Generic;
using NUnit.Framework;
using LitMotion.Adapters;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.TestTools.Constraints;
using AllocIs = UnityEngine.TestTools.Constraints.Is;
using Is = NUnit.Framework.Is;

namespace LitMotion.Tests.Runtime
{
    public class SequenceCallbackTest
    {
        ManualMotionDispatcher dispatcher;
        Action<MotionBuilder<double, NoOptions, DoubleMotionAdapter>> useDispatcher;
        Action<MotionBuilder<double, NoOptions, DoubleMotionAdapter>> useDispatcherWithLoops;

        [SetUp]
        public void SetUp()
        {
            dispatcher = new ManualMotionDispatcher();
            useDispatcher = b => b.WithScheduler(dispatcher.Scheduler);
            useDispatcherWithLoops = b => b.WithScheduler(dispatcher.Scheduler).WithLoops(3);
        }

        MotionHandle Motion(float duration)
        {
            return LMotion.Create(0f, 1f, duration).WithScheduler(dispatcher.Scheduler).RunWithoutBinding();
        }

        [Test]
        public void Test_AppendCallback_InvokedWhenReached_InPositionOrder()
        {
            var log = new List<string>();

            var a = LMotion.Create(0f, 1f, 1f)
                .WithScheduler(dispatcher.Scheduler)
                .WithOnComplete(() => log.Add("A.complete"))
                .RunWithoutBinding();
            var b = LMotion.Create(0f, 1f, 1f)
                .WithScheduler(dispatcher.Scheduler)
                .WithOnStart(() => log.Add("B.start"))
                .RunWithoutBinding();

            LSequence.Create()
                .Append(a)
                .AppendCallback(() => log.Add("callback"))
                .Append(b)
                .Run(useDispatcher);

            Assert.That(log, Is.Empty, "Callbacks must not be invoked inside Run()");

            dispatcher.Update(0.5);
            Assert.That(log, Is.Empty);

            dispatcher.Update(0.75);
            Assert.That(log, Is.EqualTo(new[] { "A.complete", "callback", "B.start" }));

            dispatcher.Update(1.0);
            Assert.That(log, Is.EqualTo(new[] { "A.complete", "callback", "B.start" }));
        }

        [Test]
        public void Test_InsertCallback_InvokedOnceAtPosition()
        {
            var count = 0;
            LSequence.Create()
                .Append(Motion(1f))
                .InsertCallback(0.5f, () => count++)
                .Run(useDispatcher);

            dispatcher.Update(0.25);
            Assert.That(count, Is.EqualTo(0));
            dispatcher.Update(0.25);
            Assert.That(count, Is.EqualTo(1));
            dispatcher.Update(0.25);
            Assert.That(count, Is.EqualTo(1));
            dispatcher.Update(1.0);
            Assert.That(count, Is.EqualTo(1));
        }

        [Test]
        public void Test_CallbackAtStart_InvokedOnFirstUpdate()
        {
            var count = 0;
            LSequence.Create()
                .AppendCallback(() => count++)
                .Append(Motion(1f))
                .Run(useDispatcher);

            Assert.That(count, Is.EqualTo(0));
            dispatcher.Update(0.1);
            Assert.That(count, Is.EqualTo(1));
            dispatcher.Update(0.1);
            Assert.That(count, Is.EqualTo(1));
        }

        [Test]
        public void Test_CallbackAtStart_NotInvokedWhilePaused()
        {
            var count = 0;
            var handle = LSequence.Create()
                .AppendCallback(() => count++)
                .Append(Motion(1f))
                .Run(useDispatcher);

            handle.Pause();
            dispatcher.Update(0.5);
            Assert.That(count, Is.EqualTo(0));

            handle.Play();
            dispatcher.Update(0.1);
            Assert.That(count, Is.EqualTo(1));
        }

        [Test]
        public void Test_CallbackAtEnd_InvokedBeforeSequenceOnComplete()
        {
            var log = new List<string>();
            LSequence.Create()
                .Append(Motion(1f))
                .AppendCallback(() => log.Add("callback"))
                .Run(b => b.WithScheduler(dispatcher.Scheduler).WithOnComplete(() => log.Add("complete")));

            dispatcher.Update(0.5);
            Assert.That(log, Is.Empty);
            dispatcher.Update(0.6);
            Assert.That(log, Is.EqualTo(new[] { "callback", "complete" }));
        }

        [Test]
        public void Test_Complete_InvokesPendingCallbacksInOrder()
        {
            var log = new List<string>();
            var handle = LSequence.Create()
                .AppendCallback(() => log.Add("0"))
                .Append(Motion(1f))
                .AppendCallback(() => log.Add("1"))
                .Append(Motion(1f))
                .AppendCallback(() => log.Add("2"))
                .Run(useDispatcher);

            dispatcher.Update(0.5);
            Assert.That(log, Is.EqualTo(new[] { "0" }));

            handle.Complete();
            Assert.That(log, Is.EqualTo(new[] { "0", "1", "2" }));

            dispatcher.Update(1.0);
            Assert.That(log, Is.EqualTo(new[] { "0", "1", "2" }));
        }

        [Test]
        public void Test_Complete_BeforeFirstUpdate_InvokesAllCallbacks()
        {
            var count = 0;
            var handle = LSequence.Create()
                .AppendCallback(() => count++)
                .Append(Motion(1f))
                .AppendCallback(() => count++)
                .Run(useDispatcher);

            handle.Complete();
            Assert.That(count, Is.EqualTo(2));
        }

        [Test]
        public void Test_Cancel_DoesNotInvokePendingCallbacks()
        {
            var count = 0;
            var child = Motion(1f);
            var handle = LSequence.Create()
                .Append(child)
                .AppendCallback(() => count++)
                .Run(useDispatcher);

            dispatcher.Update(0.5);
            handle.Cancel();
            dispatcher.Update(1.0);
            dispatcher.Update(1.0);

            Assert.That(count, Is.EqualTo(0));
            Assert.That(child.IsActive(), Is.False);
        }

        [Test]
        public void Test_Loops_Restart_InvokedEveryLoop()
        {
            int start = 0, middle = 0, end = 0;
            LSequence.Create()
                .AppendCallback(() => start++)
                .Append(Motion(1f))
                .AppendCallback(() => end++)
                .InsertCallback(0.5f, () => middle++)
                .Run(b => b.WithScheduler(dispatcher.Scheduler).WithLoops(3));

            for (int i = 0; i < 12; i++) dispatcher.Update(0.25);

            Assert.That(start, Is.EqualTo(3));
            Assert.That(middle, Is.EqualTo(3));
            Assert.That(end, Is.EqualTo(3));
        }

        [Test]
        public void Test_Loops_LargeDeltaTime_InvokedForEverySkippedLoop()
        {
            int middle = 0, end = 0;
            var handle = LSequence.Create()
                .Append(Motion(1f))
                .AppendCallback(() => end++)
                .InsertCallback(0.5f, () => middle++)
                .Run(b => b.WithScheduler(dispatcher.Scheduler).WithLoops(5));

            dispatcher.Update(0.25);
            dispatcher.Update(3.0); // time 3.25: loops 0, 1 and 2 have ended
            Assert.That(middle, Is.EqualTo(3));
            Assert.That(end, Is.EqualTo(3));

            dispatcher.Update(10.0);
            Assert.That(middle, Is.EqualTo(5));
            Assert.That(end, Is.EqualTo(5));
            Assert.That(handle.IsActive(), Is.False);
        }

        [Test]
        public void Test_Loops_Infinite_EndCallbackInvokedEveryLoop()
        {
            var end = 0;
            var handle = LSequence.Create()
                .Append(Motion(1f))
                .AppendCallback(() => end++)
                .Run(b => b.WithScheduler(dispatcher.Scheduler).WithLoops(-1));

            // 0.3 never lands exactly on a loop boundary.
            for (int i = 0; i < 34; i++) dispatcher.Update(0.3); // time 10.2
            Assert.That(end, Is.EqualTo(10));

            handle.Cancel();
        }

        [Test]
        public void Test_Loops_Yoyo_InvokedInEveryLoop_IncludingTheReturn()
        {
            int middle = 0, end = 0;
            LSequence.Create()
                .Append(Motion(1f))
                .InsertCallback(0.5f, () => middle++)
                .AppendCallback(() => end++)
                .Run(b => b.WithScheduler(dispatcher.Scheduler).WithLoops(4, LoopType.Yoyo));

            // Loop 0 goes out; at time 1 loop 1 starts its return at the end position, which is the mirrored trigger
            // point of the end callback in loop 1.
            for (int i = 0; i < 4; i++) dispatcher.Update(0.25);
            Assert.That((middle, end), Is.EqualTo((1, 2)));

            for (int i = 0; i < 4; i++) dispatcher.Update(0.25); // the return passes the middle
            Assert.That((middle, end), Is.EqualTo((2, 2)));

            for (int i = 0; i < 8; i++) dispatcher.Update(0.25);
            Assert.That((middle, end), Is.EqualTo((4, 4)));
        }

        [Test]
        public void Test_Loops_Flip_InvokedInEveryLoop()
        {
            var middle = 0;
            LSequence.Create()
                .Append(Motion(1f))
                .InsertCallback(0.5f, () => middle++)
                .Run(b => b.WithScheduler(dispatcher.Scheduler).WithLoops(4, LoopType.Flip));

            for (int i = 0; i < 6; i++) dispatcher.Update(0.25); // loop 1 (mirrored) reaches the middle at time 1.5
            Assert.That(middle, Is.EqualTo(2));
            for (int i = 0; i < 10; i++) dispatcher.Update(0.25);
            Assert.That(middle, Is.EqualTo(4));
        }

        [Test]
        public void Test_Loops_Complete_InvokesCallbacksOfRemainingLoops()
        {
            int middle = 0, end = 0;
            var handle = LSequence.Create()
                .Append(Motion(1f))
                .InsertCallback(0.5f, () => middle++)
                .AppendCallback(() => end++)
                .Run(b => b.WithScheduler(dispatcher.Scheduler).WithLoops(3));

            dispatcher.Update(0.75);
            Assert.That((middle, end), Is.EqualTo((1, 0)));

            handle.Complete();
            Assert.That((middle, end), Is.EqualTo((3, 3)));
        }

        [Test]
        public void Test_SetTime_ForwardInvokesSkipped_BackwardRearms()
        {
            int a = 0, b = 0;
            var handle = LSequence.Create()
                .Append(Motion(1f))
                .InsertCallback(0.25f, () => a++)
                .InsertCallback(0.75f, () => b++)
                .Append(Motion(1f))
                .Run(useDispatcher)
                .Preserve();

            handle.Time = 0.8;
            Assert.That((a, b), Is.EqualTo((1, 1)));

            handle.Time = 0.5;
            Assert.That((a, b), Is.EqualTo((1, 1)), "Moving backward must not invoke callbacks");

            handle.Time = 1.0;
            Assert.That((a, b), Is.EqualTo((1, 2)));

            handle.Time = 0.0;
            handle.Time = 0.3;
            Assert.That((a, b), Is.EqualTo((2, 2)));

            handle.Time = 2.0; // completes the (preserved) sequence
            Assert.That((a, b), Is.EqualTo((2, 3)));

            handle.Cancel();
        }

        [Test]
        public void Test_NegativePlaybackSpeed_DoesNotInvoke()
        {
            var count = 0;
            var handle = LSequence.Create()
                .Append(Motion(1f))
                .InsertCallback(0.5f, () => count++)
                .Run(useDispatcher);

            dispatcher.Update(0.75);
            Assert.That(count, Is.EqualTo(1));

            handle.PlaybackSpeed = -1f;
            dispatcher.Update(0.5); // playhead 0.25
            Assert.That(count, Is.EqualTo(1));

            handle.PlaybackSpeed = 1f;
            dispatcher.Update(0.5); // playhead 0.75
            Assert.That(count, Is.EqualTo(2));

            handle.Cancel();
        }

        [Test]
        public void Test_CallbackCancelsSequence_RemainingCallbacksAreNotInvoked()
        {
            var second = 0;
            var handle = default(MotionHandle);
            var a = Motion(1f);
            var b = Motion(1f);

            handle = LSequence.Create()
                .Append(a)
                .InsertCallback(0.5f, () => handle.Cancel())
                .InsertCallback(0.5f, () => second++)
                .Append(b)
                .Run(useDispatcher);

            dispatcher.Update(0.75);

            Assert.That(second, Is.EqualTo(0));
            Assert.That(handle.IsActive(), Is.False);
            Assert.That(a.IsActive(), Is.False);
            Assert.That(b.IsActive(), Is.False);

            dispatcher.Update(0.1);
            Assert.That(a.IsValid(), Is.False);
            Assert.That(b.IsValid(), Is.False);
        }

        [Test]
        public void Test_CallbackCompletesSequence_RemainingCallbacksInvokedOnce()
        {
            var log = new List<string>();
            var completed = 0;
            var handle = default(MotionHandle);

            handle = LSequence.Create()
                .Append(Motion(1f))
                .InsertCallback(0.25f, () => { log.Add("a"); handle.Complete(); })
                .InsertCallback(0.5f, () => log.Add("b"))
                .AppendCallback(() => log.Add("c"))
                .Run(b => b.WithScheduler(dispatcher.Scheduler).WithOnComplete(() => completed++));

            dispatcher.Update(0.3);
            Assert.That(log, Is.EqualTo(new[] { "a", "b", "c" }));
            Assert.That(completed, Is.EqualTo(1));

            dispatcher.Update(1.0);
            Assert.That(log, Is.EqualTo(new[] { "a", "b", "c" }));
            Assert.That(completed, Is.EqualTo(1));
        }

        [Test]
        public void Test_CallbackCompletesSequence_WhileCatchingUpSkippedLoops_EachLoopInvokedOnce()
        {
            int a = 0, b = 0;
            var handle = default(MotionHandle);

            handle = LSequence.Create()
                .Append(Motion(1f))
                .InsertCallback(0.25f, () => a++)
                .InsertCallback(0.75f, () =>
                {
                    b++;
                    if (b == 2) handle.Complete(); // re-enter while the update is still walking over skipped loops
                })
                .Run(builder => builder.WithScheduler(dispatcher.Scheduler).WithLoops(4));

            dispatcher.Update(0.1);
            dispatcher.Update(2.5); // loops 0 and 1 are skipped in one update

            Assert.That((a, b), Is.EqualTo((4, 4)));
            Assert.That(handle.IsActive(), Is.False);
        }

        [Test]
        public void Test_CallbackRewindsSequence()
        {
            var count = 0;
            var handle = default(MotionHandle);

            handle = LSequence.Create()
                .Append(Motion(1f))
                .InsertCallback(0.5f, () =>
                {
                    count++;
                    if (count == 1) handle.Time = 0.25; // jump back from inside the callback
                })
                .Run(useDispatcher);

            dispatcher.Update(0.6);
            Assert.That(count, Is.EqualTo(1));
            Assert.That(handle.Time, Is.EqualTo(0.25));
            dispatcher.Update(0.3);
            Assert.That(count, Is.EqualTo(2));
            handle.Cancel();
        }

        [Test]
        public void Test_Loops_Incremental_InvokedInEveryLoop()
        {
            var count = 0;
            LSequence.Create()
                .Append(Motion(1f))
                .InsertCallback(0.5f, () => count++)
                .Run(b => b.WithScheduler(dispatcher.Scheduler).WithLoops(3, LoopType.Incremental));

            for (int i = 0; i < 6; i++) dispatcher.Update(0.25);
            Assert.That(count, Is.EqualTo(2));
            for (int i = 0; i < 10; i++) dispatcher.Update(0.25);
            Assert.That(count, Is.EqualTo(3));
        }

        [TestCase(LoopType.Restart)]
        [TestCase(LoopType.Yoyo)]
        [TestCase(LoopType.Flip)]
        [TestCase(LoopType.Incremental)]
        public void Test_ZeroLengthLoops_InvokedOncePerLoop(LoopType loopType)
        {
            var hits = 0;
            LSequence.Create()
                .AppendCallback(() => hits++)
                .Run(b => b.WithScheduler(dispatcher.Scheduler).WithLoops(4, loopType));

            dispatcher.Update(0.125);
            Assert.That(hits, Is.EqualTo(4));
        }

        [Test]
        public void Test_Loops_Incremental_CompleteWithEase_InvokesEveryLoop()
        {
            var hits = 0;
            LSequence.Create()
                .AppendInterval(1f)
                .InsertCallback(0.5f, () => hits++)
                .Run(b => b.WithScheduler(dispatcher.Scheduler).WithLoops(3, LoopType.Incremental).WithEase(Ease.OutQuad))
                .Complete();

            Assert.That(hits, Is.EqualTo(3));
        }

        [Test]
        public void Test_Loops_DelayEveryLoop_CallbackOfTheEndedLoopIsNotDelayed()
        {
            // skipValuesDuringDelay (default of WithDelay): the sequence is not evaluated during the delays,
            // but the callbacks of the loop that has just ended are still invoked in that update.
            var hits = 0;
            var completedLoops = 0;
            LSequence.Create()
                .AppendInterval(1f)
                .InsertCallback(0.5f, () => hits++)
                .Run(b => b.WithScheduler(dispatcher.Scheduler)
                    .WithDelay(0.5f, DelayType.EveryLoop)
                    .WithLoops(3)
                    .WithOnLoopComplete(x => completedLoops = x));

            dispatcher.Update(0.75); // loop 0, playhead 0.25
            Assert.That(hits, Is.EqualTo(0));
            dispatcher.Update(1.0);  // in the delay of loop 1
            Assert.That(completedLoops, Is.EqualTo(1));
            Assert.That(hits, Is.EqualTo(1));
        }

        [Test]
        public void Test_CallbackAtStart_WithDelay_InvokedWhenTheDelayEndsExactly()
        {
            var hits = 0;
            LSequence.Create()
                .AppendCallback(() => hits++)
                .AppendInterval(1f)
                .Run(b => b.WithScheduler(dispatcher.Scheduler).WithDelay(1f));

            dispatcher.Update(1.0);
            Assert.That(hits, Is.EqualTo(1));
        }

        [TestCase(LoopType.Yoyo)]
        [TestCase(LoopType.Flip)]
        public void Test_NegativePlaybackSpeed_DuringTheReturnLoop_DoesNotInvoke(LoopType loopType)
        {
            var hits = 0;
            var sequence = LSequence.Create()
                .AppendInterval(1f)
                .InsertCallback(0.5f, () => hits++)
                .Run(b => b.WithScheduler(dispatcher.Scheduler).WithLoops(2, loopType));

            sequence.Time = 1.75; // out (0.5) and back (mirrored 0.5 at time 1.5)
            Assert.That(hits, Is.EqualTo(2));

            sequence.PlaybackSpeed = -1f;
            dispatcher.Update(0.5); // time 1.25: backward over the mirrored trigger point
            Assert.That(hits, Is.EqualTo(2));

            sequence.PlaybackSpeed = 1f;
            dispatcher.Update(0.5); // time 1.75 again
            Assert.That(hits, Is.EqualTo(3));
            sequence.Cancel();
        }

        [Test]
        public void Test_Loops_InfiniteBeyondTheRangeOfTheLoopCounter()
        {
            // MotionData counts loops in 16 bits; the callbacks use their own 64-bit loop index.
            var hits = 0;
            var sequence = LSequence.Create()
                .AppendInterval(1f / 64)
                .InsertCallback(1f / 128, () => hits++)
                .Run(b => b.WithScheduler(dispatcher.Scheduler).WithLoops(-1));

            sequence.Time = 65535.75 / 64;
            Assert.That(hits, Is.EqualTo(65536));
            sequence.Time = 65536.75 / 64;
            Assert.That(hits, Is.EqualTo(65537));
            sequence.Cancel();
        }

        [Test]
        public void Test_NestedSequence_ParentLoops_ChildCallbacksInvokedEveryParentLoop()
        {
            var hits = 0;
            var child = LSequence.Create()
                .AppendInterval(1f)
                .AppendCallback(() => hits++)
                .Run(useDispatcher);
            LSequence.Create()
                .Append(child)
                .Run(b => b.WithScheduler(dispatcher.Scheduler).WithLoops(2));

            dispatcher.Update(0.75);
            dispatcher.Update(0.5);   // the parent wraps: the child is evaluated at 0.25 only, but passed its end
            Assert.That(hits, Is.EqualTo(1));
            dispatcher.Update(0.875); // the parent completes
            Assert.That(hits, Is.EqualTo(2));
        }

        [Test]
        public void Test_NestedSequence_ParentYoyo_ChildIsReArmed()
        {
            var hits = 0;
            var child = LSequence.Create()
                .AppendInterval(1f)
                .InsertCallback(0.5f, () => hits++)
                .Run(useDispatcher);
            LSequence.Create()
                .Append(child)
                .Run(b => b.WithScheduler(dispatcher.Scheduler).WithLoops(4, LoopType.Yoyo));

            // The child's own time goes back during the return of the parent: its callback is only invoked when it
            // moves forward again (once per out-going loop of the parent).
            for (int i = 0; i < 16; i++) dispatcher.Update(0.25);
            Assert.That(hits, Is.EqualTo(2));
        }

        [Test]
        public void Test_NestedSequence_InnerCallbacksInvoked()
        {
            int innerMiddle = 0, innerEnd = 0;
            var inner = LSequence.Create()
                .Append(Motion(1f))
                .InsertCallback(0.5f, () => innerMiddle++)
                .AppendCallback(() => innerEnd++)
                .Run(useDispatcher);

            LSequence.Create()
                .AppendInterval(0.5f)
                .Append(inner)
                .Run(useDispatcher);

            dispatcher.Update(0.75);
            Assert.That((innerMiddle, innerEnd), Is.EqualTo((0, 0)));
            dispatcher.Update(0.25);
            Assert.That((innerMiddle, innerEnd), Is.EqualTo((1, 0)));
            dispatcher.Update(1.0);
            Assert.That((innerMiddle, innerEnd), Is.EqualTo((1, 1)));
        }

        [Test]
        public void Test_Join_AfterAppendCallback_IsInsertedAtCallbackPosition()
        {
            var handle = LSequence.Create()
                .Append(Motion(1f))
                .AppendCallback(() => { })
                .Join(Motion(0.5f))
                .Run(useDispatcher);

            Assert.That(handle.Duration, Is.EqualTo(1.5f));
            handle.Cancel();
        }

        [Test]
        public void Test_InsertCallback_ExtendsDuration()
        {
            var count = 0;
            var handle = LSequence.Create()
                .Append(Motion(1f))
                .InsertCallback(2f, () => count++)
                .Run(useDispatcher);

            Assert.That(handle.Duration, Is.EqualTo(2f));
            dispatcher.Update(1.5);
            Assert.That(count, Is.EqualTo(0));
            dispatcher.Update(0.5);
            Assert.That(count, Is.EqualTo(1));
            Assert.That(handle.IsActive(), Is.False);
        }

        [Test]
        public void Test_CallbackOnlySequence()
        {
            var count = 0;
            var handle = LSequence.Create()
                .AppendCallback(() => count++)
                .Run(useDispatcher);

            Assert.That(count, Is.EqualTo(0));
            dispatcher.Update(0.01);
            Assert.That(count, Is.EqualTo(1));
            Assert.That(handle.IsActive(), Is.False);

            count = 0;
            LSequence.Create()
                .AppendCallback(() => count++)
                .Run(b => b.WithScheduler(dispatcher.Scheduler).WithLoops(3));
            dispatcher.Update(0.01);
            Assert.That(count, Is.EqualTo(3));
        }

        [Test]
        public void Test_ExceptionInCallback_IsReported_OtherCallbacksStillInvoked()
        {
            LogAssert.Expect(LogType.Exception, "Exception: Test");

            var count = 0;
            LSequence.Create()
                .Append(Motion(1f))
                .InsertCallback(0.25f, () => throw new Exception("Test"))
                .InsertCallback(0.25f, () => count++)
                .Run(useDispatcher);

            dispatcher.Update(0.5);
            Assert.That(count, Is.EqualTo(1));
        }

        [Test]
        public void Test_InvalidArguments()
        {
            var builder = LSequence.Create();
            Assert.Throws<ArgumentNullException>(() => builder.AppendCallback(null));
            Assert.Throws<ArgumentNullException>(() => builder.InsertCallback(0f, null));
            Assert.Throws<ArgumentOutOfRangeException>(() => builder.InsertCallback(-1f, () => { }));
            Assert.Throws<ArgumentOutOfRangeException>(() => builder.InsertCallback(float.NaN, () => { }));
            Assert.Throws<ArgumentOutOfRangeException>(() => builder.InsertCallback(float.PositiveInfinity, () => { }));

            builder.Run(useDispatcher);
            Assert.Throws<InvalidOperationException>(() => builder.AppendCallback(() => { }));
            Assert.Throws<InvalidOperationException>(() => builder.InsertCallback(0f, () => { }));
        }

        [Test]
        public void Test_DisposedBuilder_ReleasesCallbacks()
        {
            var count = 0;
            var builder = LSequence.Create().AppendCallback(() => count++);
            builder.Dispose();

            // The pooled builder must not carry the callback over to the next sequence.
            LSequence.Create().Append(Motion(1f)).Run(useDispatcher);
            dispatcher.Update(2.0);
            Assert.That(count, Is.EqualTo(0));
        }

        [Test]
        public void Test_NoAllocation_BuildSeekAndComplete()
        {
            var count = 0;
            Action callback = () => count++;

            MotionHandle Build(int loops)
            {
                return LSequence.Create()
                    .AppendCallback(callback)
                    .Append(Motion(1f))
                    .InsertCallback(0.5f, callback)
                    .AppendCallback(callback)
                    .Run(loops == 1 ? useDispatcher : useDispatcherWithLoops);
            }

            void Seek(MotionHandle handle)
            {
                // forward, backward, across loop boundaries in both directions, and to the end
                handle.Time = 0.3;
                handle.Time = 0.7;
                handle.Time = 2.6;
                handle.Time = 1.2;
                handle.Time = 0.1;
                handle.Time = 3.0;
            }

            // Warm up pools and storages.
            for (int i = 0; i < 3; i++)
            {
                Build(1).Complete();
                Seek(Build(3));
                dispatcher.Update(0.1);
            }

            count = 0;
            Assert.That(() => Build(1).Complete(), AllocIs.Not.AllocatingGCMemory());
            Assert.That(count, Is.EqualTo(3));

            // 0.3: start; 0.7: middle; 2.6: end of loop 0, loop 1, start and middle of loop 2;
            // 1.2 and 0.1: backward; 3.0: middle and end of loop 0, loops 1 and 2.
            count = 0;
            Assert.That(() => Seek(Build(3)), AllocIs.Not.AllocatingGCMemory());
            Assert.That(count, Is.EqualTo(16));
        }

        [Test]
        public void Test_NoAdditionalAllocation_WhenUpdating()
        {
            var count = 0;
            Action callback = () => count++;
            var plainDispatcher = new ManualMotionDispatcher();
            Action<MotionBuilder<double, NoOptions, DoubleMotionAdapter>> usePlainDispatcher = b => b.WithScheduler(plainDispatcher.Scheduler).WithLoops(3);

            void BuildWithCallbacks()
            {
                LSequence.Create()
                    .AppendCallback(callback)
                    .Append(Motion(1f))
                    .InsertCallback(0.5f, callback)
                    .AppendCallback(callback)
                    .Run(useDispatcherWithLoops);
            }

            void BuildPlain()
            {
                LSequence.Create()
                    .Append(LMotion.Create(0f, 1f, 1f).WithScheduler(plainDispatcher.Scheduler).RunWithoutBinding())
                    .Run(usePlainDispatcher);
            }

            for (int i = 0; i < 3; i++)
            {
                BuildWithCallbacks();
                BuildPlain();
                for (int j = 0; j < 14; j++)
                {
                    dispatcher.Update(0.25);
                    plainDispatcher.Update(0.25);
                }
            }

            BuildWithCallbacks();
            BuildPlain();
            count = 0;
            var withCallbacks = GCAllocationCounter.Count(() =>
            {
                for (int j = 0; j < 14; j++) dispatcher.Update(0.25);
            });
            var plain = GCAllocationCounter.Count(() =>
            {
                for (int j = 0; j < 14; j++) plainDispatcher.Update(0.25);
            });

            Assert.That(count, Is.EqualTo(9));
            Assert.That(withCallbacks, Is.EqualTo(plain));
        }
    
        // Non-monotonic ease: the callback (0.9) is first reached at 0.36 (OutBounce is about 1 there). Going back to 0.5,
        // where the eased position (about 0.77) is below the callback again, does not re-arm it: its trigger point (0.36)
        // is before 0.5. Only going back before the time it was triggered re-arms it.
        [Test]
        public void Test_NonMonotonicEase_PositionFallingBackDoesNotRearm()
        {
            var hits = 0;
            var sequence = LSequence.Create()
                .AppendInterval(1f)
                .InsertCallback(0.9f, () => hits++)
                .Run(b => b.WithScheduler(dispatcher.Scheduler).WithEase(Ease.OutBounce));

            sequence.Time = 0.36;
            Assert.That(hits, Is.EqualTo(1));
            sequence.Time = 0.8;
            Assert.That(hits, Is.EqualTo(1));
            sequence.Time = 0.5;
            sequence.Time = 0.8;
            Assert.That(hits, Is.EqualTo(1));

            sequence.Time = 0.2;
            sequence.Time = 0.5;
            Assert.That(hits, Is.EqualTo(1), "0.5 is sampled below the callback position");
            sequence.Time = 1.0;
            Assert.That(hits, Is.EqualTo(2), "the end of the loop reaches every callback");
        }

        // Documented: with a non-monotonic ease, whether a callback is triggered before the end of the loop depends on the
        // sampled times (here 0.36 is sampled in one case and not in the other).
        [Test]
        public void Test_NonMonotonicEase_TriggerDependsOnTheSampledTimes()
        {
            var staged = 0;
            var jumped = 0;
            var a = LSequence.Create().AppendInterval(1f).InsertCallback(0.9f, () => staged++)
                .Run(b => b.WithScheduler(dispatcher.Scheduler).WithEase(Ease.OutBounce));
            var b = LSequence.Create().AppendInterval(1f).InsertCallback(0.9f, () => jumped++)
                .Run(b => b.WithScheduler(dispatcher.Scheduler).WithEase(Ease.OutBounce));

            a.Time = 0.36;
            a.Time = 0.5;
            b.Time = 0.5;

            Assert.That(staged, Is.EqualTo(1));
            Assert.That(jumped, Is.Zero);
            a.Cancel();
            b.Cancel();
        }

        // README 1.1: children that start after the new position first, then the children that started before the callback, then
        // the callback, then the child that starts exactly at the callback position (it starts after the callback).
        [TestCase(false)]
        [TestCase(true)]
        public void Test_ChildStartingExactlyAtTheCallbackPosition_StartsAfterTheCallback(bool runner)
        {
            var events = new List<string>();
            var earlier = LMotion.Create(0f, 1f, 0.5f).WithScheduler(dispatcher.Scheduler).WithImmediateBind(false)
                .Bind(_ => events.Add("earlier child"));
            var same = LMotion.Create(0f, 1f, 0.5f).WithScheduler(dispatcher.Scheduler)
                .WithOnStart(() => events.Add("same-position child")).RunWithoutBinding();
            var future = LMotion.Create(0f, 1f, 0.25f).WithScheduler(dispatcher.Scheduler).WithImmediateBind(false)
                .WithDelay(0f, skipValuesDuringDelay: false)
                .Bind(_ => events.Add("future child"));
            var sequence = LSequence.Create()
                .Append(earlier)
                .AppendCallback(() => events.Add("callback"))
                .Append(same)
                .Insert(0.75f, future)
                .Run(useDispatcher);

            if (runner) dispatcher.Update(0.5);
            else sequence.Time = 0.5;

            Assert.That(events, Is.EqualTo(new[] { "future child", "earlier child", "callback", "same-position child" }));
        }
}
}
