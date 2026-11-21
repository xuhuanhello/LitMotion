using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.TestTools.Constraints;
using AllocIs = UnityEngine.TestTools.Constraints.Is;
using Is = NUnit.Framework.Is;

namespace LitMotion.Tests.Runtime
{
    public class MotionStartCallbackTest
    {
        ManualMotionDispatcher dispatcher;

        [SetUp]
        public void SetUp()
        {
            dispatcher = new ManualMotionDispatcher();
        }

        MotionBuilder<float, NoOptions, Adapters.FloatMotionAdapter> Create(float duration)
        {
            return LMotion.Create(0f, 1f, duration).WithScheduler(dispatcher.Scheduler);
        }

        [Test]
        public void Test_NoDelay_InvokedOnFirstUpdate_BeforeTheValueIsBound()
        {
            var log = new List<string>();
            Create(1f)
                .WithOnStart(() => log.Add("start"))
                .Bind(x => log.Add("bind"));

            // ImmediateBind binds the start value on creation, but OnStart is not invoked yet.
            Assert.That(log, Is.EqualTo(new[] { "bind" }));

            dispatcher.Update(0.1);
            Assert.That(log, Is.EqualTo(new[] { "bind", "start", "bind" }));

            dispatcher.Update(0.1);
            dispatcher.Update(1.0);
            Assert.That(log.FindAll(x => x == "start").Count, Is.EqualTo(1));
        }

        [Test]
        public void Test_WithDelay_InvokedWhenDelayElapses()
        {
            var count = 0;
            Create(1f)
                .WithDelay(0.5f)
                .WithOnStart(() => count++)
                .RunWithoutBinding();

            dispatcher.Update(0.25);
            Assert.That(count, Is.EqualTo(0));
            dispatcher.Update(0.25);
            Assert.That(count, Is.EqualTo(1));
            dispatcher.Update(0.25);
            Assert.That(count, Is.EqualTo(1));
        }

        [Test]
        public void Test_Loops_InvokedOnce()
        {
            var restart = 0;
            var everyLoopDelay = 0;
            var yoyo = 0;

            Create(0.5f).WithLoops(3).WithOnStart(() => restart++).RunWithoutBinding();
            Create(0.5f).WithLoops(3).WithDelay(0.25f, DelayType.EveryLoop).WithOnStart(() => everyLoopDelay++).RunWithoutBinding();
            Create(0.5f).WithLoops(4, LoopType.Yoyo).WithOnStart(() => yoyo++).RunWithoutBinding();

            for (int i = 0; i < 30; i++) dispatcher.Update(0.1);

            Assert.That(restart, Is.EqualTo(1));
            Assert.That(everyLoopDelay, Is.EqualTo(1));
            Assert.That(yoyo, Is.EqualTo(1));
        }

        [Test]
        public void Test_InfiniteLoops_InvokedOnce()
        {
            var count = 0;
            var handle = Create(0.5f).WithLoops(-1).WithOnStart(() => count++).RunWithoutBinding();
            for (int i = 0; i < 30; i++) dispatcher.Update(0.1);
            Assert.That(count, Is.EqualTo(1));
            handle.Cancel();
        }

        [Test]
        public void Test_CanceledBeforeStart_NotInvoked()
        {
            var count = 0;
            var delayed = Create(1f).WithDelay(1f).WithOnStart(() => count++).RunWithoutBinding();
            var notUpdated = Create(1f).WithOnStart(() => count++).RunWithoutBinding();

            notUpdated.Cancel();
            dispatcher.Update(0.5);
            delayed.Cancel();
            dispatcher.Update(2.0);

            Assert.That(count, Is.EqualTo(0));
        }

        [Test]
        public void Test_Complete_BeforeStart_InvokesOnStartBeforeValueAndOnComplete()
        {
            var log = new List<string>();
            var handle = Create(1f)
                .WithDelay(1f)
                .WithOnStart(() => log.Add("start"))
                .WithOnComplete(() => log.Add("complete"))
                .Bind(x => log.Add($"bind:{x}"));

            dispatcher.Update(0.5);
            handle.Complete();
            Assert.That(log, Is.EqualTo(new[] { "bind:0", "start", "bind:1", "complete" }));

            dispatcher.Update(1.0);
            Assert.That(log.FindAll(x => x == "start").Count, Is.EqualTo(1));
        }

        [Test]
        public void Test_Complete_AfterStart_DoesNotInvokeAgain()
        {
            var count = 0;
            var handle = Create(1f).WithOnStart(() => count++).RunWithoutBinding();
            dispatcher.Update(0.5);
            handle.Complete();
            Assert.That(count, Is.EqualTo(1));
        }

        [Test]
        public void Test_SettingTime_PastDelay_Invokes()
        {
            var count = 0;
            var handle = Create(1f).WithDelay(1f).WithOnStart(() => count++).RunWithoutBinding();

            handle.Time = 0.5;
            Assert.That(count, Is.EqualTo(0));
            handle.Time = 1.5;
            Assert.That(count, Is.EqualTo(1));
            handle.Time = 0.0;
            handle.Time = 1.5;
            Assert.That(count, Is.EqualTo(1));
            handle.Cancel();
        }

        [Test]
        public void Test_InSequence_InvokedWhenPlayheadReachesMotion()
        {
            var count = 0;
            var child = Create(1f).WithDelay(0.25f).WithOnStart(() => count++).RunWithoutBinding();

            LSequence.Create()
                .Append(Create(1f).RunWithoutBinding())
                .Append(child)
                .Run(b => b.WithScheduler(dispatcher.Scheduler).WithLoops(2));

            dispatcher.Update(1.0);
            Assert.That(count, Is.EqualTo(0));
            dispatcher.Update(0.2); // child time 0.2 < its delay
            Assert.That(count, Is.EqualTo(0));
            dispatcher.Update(0.1);
            Assert.That(count, Is.EqualTo(1));

            for (int i = 0; i < 30; i++) dispatcher.Update(0.1); // second loop of the sequence
            Assert.That(count, Is.EqualTo(1));
        }

        [Test]
        public void Test_InSequence_SequenceComplete_InvokesPendingOnStart()
        {
            var log = new List<string>();
            var child = Create(1f)
                .WithOnStart(() => log.Add("start"))
                .WithOnComplete(() => log.Add("complete"))
                .RunWithoutBinding();

            var handle = LSequence.Create()
                .AppendInterval(1f)
                .Append(child)
                .Run(b => b.WithScheduler(dispatcher.Scheduler));

            dispatcher.Update(0.5);
            handle.Complete();
            Assert.That(log, Is.EqualTo(new[] { "start", "complete" }));
        }

        [Test]
        public void Test_SequenceOnStart()
        {
            var count = 0;
            LSequence.Create()
                .Append(Create(1f).RunWithoutBinding())
                .Run(b => b.WithScheduler(dispatcher.Scheduler).WithDelay(0.5f).WithOnStart(() => count++));

            dispatcher.Update(0.25);
            Assert.That(count, Is.EqualTo(0));
            dispatcher.Update(0.5);
            Assert.That(count, Is.EqualTo(1));
        }

        [Test]
        public void Test_PausedBeforeStart_DeferredUntilResumed()
        {
            var count = 0;
            var handle = Create(1f).WithOnStart(() => count++).RunWithoutBinding();

            handle.Pause();
            dispatcher.Update(0.5);
            Assert.That(count, Is.EqualTo(0));

            handle.Play();
            dispatcher.Update(0.1);
            Assert.That(count, Is.EqualTo(1));
            handle.Cancel();
        }

        [Test]
        public void Test_OnStartCancelsMotion_SkipsRestOfUpdate()
        {
            var handle = default(MotionHandle);
            var binds = 0;
            var completed = false;
            var canceled = false;

            handle = Create(1f)
                .WithOnStart(() => handle.Cancel())
                .WithOnComplete(() => completed = true)
                .WithOnCancel(() => canceled = true)
                .Bind(x => binds++);
            Assert.That(binds, Is.EqualTo(1));

            dispatcher.Update(0.1);
            Assert.That(canceled, Is.True);
            Assert.That(binds, Is.EqualTo(1));

            dispatcher.Update(2.0);
            Assert.That(completed, Is.False);
            Assert.That(handle.IsValid(), Is.False);
        }

        [Test]
        public void Test_OnStartCompletesMotion_OnCompleteInvokedOnce()
        {
            var handle = default(MotionHandle);
            var completed = 0;
            handle = Create(1f)
                .WithOnStart(() => handle.Complete())
                .WithOnComplete(() => completed++)
                .RunWithoutBinding();

            dispatcher.Update(0.1);
            dispatcher.Update(0.1);
            Assert.That(completed, Is.EqualTo(1));
        }

        [Test]
        public void Test_ExceptionInOnStart_IsReported_MotionContinues()
        {
            LogAssert.Expect(LogType.Exception, "Exception: Test");

            var completed = false;
            Create(0.5f)
                .WithOnStart(() => throw new Exception("Test"))
                .WithOnComplete(() => completed = true)
                .RunWithoutBinding();

            dispatcher.Update(0.1);
            dispatcher.Update(1.0);
            Assert.That(completed, Is.True);
        }

        [Test]
        public void Test_ZeroDuration_StartThenComplete()
        {
            var log = new List<string>();
            Create(0f)
                .WithOnStart(() => log.Add("start"))
                .WithOnComplete(() => log.Add("complete"))
                .RunWithoutBinding();

            dispatcher.Update(0.1);
            Assert.That(log, Is.EqualTo(new[] { "start", "complete" }));
        }

        [Test]
        public void Test_MultipleCallbacks_AreCombined()
        {
            var a = 0;
            var b = 0;
            Create(1f).WithOnStart(() => a++).WithOnStart(() => b++).RunWithoutBinding();
            dispatcher.Update(0.1);
            Assert.That((a, b), Is.EqualTo((1, 1)));
        }

        [TestCase(1f, 3, 2.5)]   // the update ends in the delay of the second loop
        [TestCase(0f, 3, 1.25)]  // zero duration: the first loop ends when the first delay ends
        [TestCase(0f, -1, 1.25)] // zero duration, infinite loops: never "Playing"
        public void Test_EveryLoopDelay_OnStartPrecedesTheFirstLoopCompletion(float duration, int loops, double deltaTime)
        {
            var events = new List<string>();
            var handle = Create(duration)
                .WithDelay(1f, DelayType.EveryLoop)
                .WithLoops(loops)
                .WithOnStart(() => events.Add("start"))
                .WithOnLoopComplete(x => events.Add($"loop={x}"))
                .RunWithoutBinding();

            dispatcher.Update(deltaTime);

            Assert.That(events, Is.EqualTo(new[] { "start", "loop=1" }));
            handle.TryCancel();
        }

        [Test]
        public void Test_GotoTheEndWhilePaused_InvokesOnStartBeforeOnComplete()
        {
            var events = new List<string>();
            var handle = Create(1f)
                .WithOnStart(() => events.Add("start"))
                .WithOnComplete(() => events.Add("complete"))
                .RunWithoutBinding();

            handle.Goto(1.0);
            dispatcher.Update(0.25);

            Assert.That(events, Is.EqualTo(new[] { "start", "complete" }));
            Assert.That(handle.IsValid(), Is.False);
        }

        [Test]
        public void Test_UsedBuilder_Throws()
        {
            var builder = Create(1f);
            builder.RunWithoutBinding();
            Assert.Throws<InvalidOperationException>(() => builder.WithOnStart(() => { }));
        }

        [Test]
        public void Test_PooledBuilderDoesNotKeepCallback()
        {
            var count = 0;
            Create(1f).WithOnStart(() => count++).RunWithoutBinding().Cancel();
            Create(1f).RunWithoutBinding();
            dispatcher.Update(0.5);
            Assert.That(count, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator Test_PlayerLoop()
        {
            var count = 0;
            var handle = LMotion.Create(0f, 1f, 0.5f)
                .WithDelay(0.1f)
                .WithOnStart(() => count++)
                .RunWithoutBinding();

            Assert.That(count, Is.EqualTo(0));
            yield return handle.ToYieldInstruction();
            Assert.That(count, Is.EqualTo(1));
        }

        [Test]
        public void Test_NoAllocation_CreateSeekAndComplete()
        {
            var count = 0;
            Action callback = () => count++;

            void Run()
            {
                var a = Create(0.5f).WithDelay(0.5f).WithOnStart(callback).RunWithoutBinding();
                a.Time = 0.25;
                a.Time = 0.75; // OnStart through Time
                a.Cancel();
                Create(0.5f).WithOnStart(callback).RunWithoutBinding().Complete(); // OnStart through Complete()
            }

            for (int i = 0; i < 3; i++)
            {
                Run();
                dispatcher.Update(0.1);
            }

            count = 0;
            Assert.That(Run, AllocIs.Not.AllocatingGCMemory());
            Assert.That(count, Is.EqualTo(2));
        }

        [Test]
        public void Test_SeveralCallbacksOnOneBuilder_AllocateAMulticastDelegate()
        {
            // Documented: combining callbacks allocates (Delegate.Combine); one cached delegate does not.
            Action callback = () => { };
            for (int i = 0; i < 3; i++)
            {
                Create(1f).WithOnStart(callback).RunWithoutBinding().Complete();
                Create(1f).WithOnStart(callback).WithOnStart(callback).RunWithoutBinding().Complete();
                dispatcher.Update(0.1);
            }

            Assert.That(() => Create(1f).WithOnStart(callback).RunWithoutBinding().Complete(), AllocIs.Not.AllocatingGCMemory());
            Assert.That(() => Create(1f).WithOnStart(callback).WithOnStart(callback).RunWithoutBinding().Complete(), AllocIs.AllocatingGCMemory());
        }

        [Test]
        public void Test_NoAdditionalAllocation_WhenUpdating()
        {
            var count = 0;
            Action callback = () => count++;
            var plainDispatcher = new ManualMotionDispatcher();

            for (int i = 0; i < 3; i++)
            {
                Create(0.5f).WithOnStart(callback).RunWithoutBinding();
                LMotion.Create(0f, 1f, 0.5f).WithScheduler(plainDispatcher.Scheduler).RunWithoutBinding();
                for (int j = 0; j < 4; j++)
                {
                    dispatcher.Update(0.25);
                    plainDispatcher.Update(0.25);
                }
            }

            Create(0.5f).WithOnStart(callback).RunWithoutBinding();
            LMotion.Create(0f, 1f, 0.5f).WithScheduler(plainDispatcher.Scheduler).RunWithoutBinding();
            count = 0;
            var withOnStart = GCAllocationCounter.Count(() =>
            {
                for (int j = 0; j < 4; j++) dispatcher.Update(0.25);
            });
            var plain = GCAllocationCounter.Count(() =>
            {
                for (int j = 0; j < 4; j++) plainDispatcher.Update(0.25);
            });

            Assert.That(count, Is.EqualTo(1));
            Assert.That(withOnStart, Is.EqualTo(plain));
        }
    }
}
