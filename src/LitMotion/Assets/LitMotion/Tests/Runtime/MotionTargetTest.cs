using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.TestTools.Constraints;
using AllocIs = UnityEngine.TestTools.Constraints.Is;
using Is = NUnit.Framework.Is;
using Object = UnityEngine.Object;

namespace LitMotion.Tests.Runtime
{
    public class MotionTargetTest
    {
        ManualMotionDispatcher dispatcher;

        [SetUp]
        public void SetUp()
        {
            dispatcher = new ManualMotionDispatcher();
            MotionTargetRegistry.Clear();
        }

        MotionBuilder<float, NoOptions, Adapters.FloatMotionAdapter> Create(float duration)
        {
            return LMotion.Create(0f, 1f, duration).WithScheduler(dispatcher.Scheduler);
        }

        [Test]
        public void Test_CancelAll()
        {
            var target = new GameObject("Target");
            var other = new GameObject("Other");
            try
            {
                var canceled = 0;
                var a = Create(1f).WithOnCancel(() => canceled++).RunWithoutBinding().SetTarget(target);
                var b = Create(1f).WithOnCancel(() => canceled++).RunWithoutBinding().SetTarget(target);
                var c = Create(1f).RunWithoutBinding().SetTarget(other);
                var d = Create(1f).RunWithoutBinding();

                Assert.That(LMotion.CancelAll(target), Is.EqualTo(2));
                Assert.That(canceled, Is.EqualTo(2));
                Assert.That(a.IsActive(), Is.False);
                Assert.That(b.IsActive(), Is.False);
                Assert.That(c.IsActive(), Is.True);
                Assert.That(d.IsActive(), Is.True);

                Assert.That(LMotion.CancelAll(target), Is.EqualTo(0));
                Assert.That(MotionTargetRegistry.GetEntryCount(target), Is.EqualTo(0));
            }
            finally
            {
                Object.DestroyImmediate(target);
                Object.DestroyImmediate(other);
            }
        }

        [Test]
        public void Test_CompleteAll()
        {
            var target = new object();
            float value = 0f;
            var completed = 0;
            var canceled = 0;

            var finite = Create(1f).WithOnComplete(() => completed++).Bind(x => value = x).SetTarget(target);
            var infinite = Create(1f).WithLoops(-1).WithOnCancel(() => canceled++).RunWithoutBinding().SetTarget(target);
            var preserved = Create(0.5f).RunWithoutBinding().Preserve().SetTarget(target);
            dispatcher.Update(0.6); // `preserved` has completed

            Assert.That(LMotion.CompleteAll(target), Is.EqualTo(2));
            Assert.That(value, Is.EqualTo(1f));
            Assert.That(completed, Is.EqualTo(1));
            Assert.That(canceled, Is.EqualTo(1));
            Assert.That(finite.IsActive(), Is.False);
            Assert.That(infinite.IsActive(), Is.False);

            // The preserved motion is left as is and stays registered.
            Assert.That(preserved.IsActive(), Is.True);
            Assert.That(LMotion.CancelAll(target), Is.EqualTo(1));
            Assert.That(preserved.IsActive(), Is.False);
        }

        [Test]
        public void Test_ButtonMashing_OnlyLatestMotionRuns()
        {
            var target = new object();
            var handles = new MotionHandle[20];
            for (int i = 0; i < handles.Length; i++)
            {
                LMotion.CancelAll(target);
                handles[i] = Create(0.3f).RunWithoutBinding().SetTarget(target);
                dispatcher.Update(0.05);
            }

            for (int i = 0; i < handles.Length - 1; i++)
            {
                Assert.That(handles[i].IsActive(), Is.False);
            }
            Assert.That(handles[^1].IsActive(), Is.True);
            Assert.That(MotionTargetRegistry.GetEntryCount(target), Is.EqualTo(1));
        }

        [Test]
        public void Test_CompletedMotions_DoNotGrowTheRegistry()
        {
            // Same target: entries of finished motions are dropped when the list is full.
            var target = new object();
            for (int i = 0; i < 1000; i++)
            {
                Create(0.1f).RunWithoutBinding().SetTarget(target);
                dispatcher.Update(0.2);
            }
            Assert.That(MotionTargetRegistry.GetEntryCount(target), Is.LessThanOrEqualTo(4));

            // Many targets: targets whose motions have finished are dropped by the sweep.
            for (int round = 0; round < 50; round++)
            {
                for (int i = 0; i < 50; i++)
                {
                    Create(0.1f).RunWithoutBinding().SetTarget(new object());
                }
                dispatcher.Update(0.2);
            }
            Assert.That(MotionTargetRegistry.TargetCount, Is.LessThan(250));
        }

        [Test]
        public void Test_DestroyedTarget()
        {
            var target = new GameObject("Target");
            var handle = Create(1f).RunWithoutBinding().SetTarget(target);

            // SetTarget does not link the lifetime of the motion to the target.
            Object.DestroyImmediate(target);
            Assert.That(handle.IsActive(), Is.True);

            // The destroyed object can still be used as the key.
            Assert.That(LMotion.CancelAll(target), Is.EqualTo(1));
            Assert.That(handle.IsActive(), Is.False);
        }

        [Test]
        public void Test_DestroyedTarget_WithAddTo()
        {
            var target = new GameObject("Target");
            var handle = Create(1f).RunWithoutBinding().AddTo(target).SetTarget(target);

            Object.DestroyImmediate(target);
            Assert.That(handle.IsActive(), Is.False);
            Assert.That(LMotion.CancelAll(target), Is.EqualTo(0));
            Assert.That(MotionTargetRegistry.TargetCount, Is.EqualTo(0));
        }

        [Test]
        public void Test_SetTarget_ReplacesPreviousTarget()
        {
            var a = new object();
            var b = new object();
            var handle = Create(1f).RunWithoutBinding().SetTarget(a).SetTarget(b);

            Assert.That(LMotion.CancelAll(a), Is.EqualTo(0));
            Assert.That(handle.IsActive(), Is.True);
            Assert.That(LMotion.CancelAll(b), Is.EqualTo(1));
        }

        [Test]
        public void Test_SetTarget_SameTargetTwice()
        {
            var target = new object();
            Create(1f).RunWithoutBinding().SetTarget(target).SetTarget(target);
            Assert.That(MotionTargetRegistry.GetEntryCount(target), Is.EqualTo(1));
            Assert.That(LMotion.CancelAll(target), Is.EqualTo(1));
        }

        [Test]
        public void Test_StringTarget_ComparedByValue()
        {
            var handle = Create(1f).RunWithoutBinding().SetTarget("popup");
            var sameValue = new string("popup".ToCharArray());
            Assert.That(LMotion.CancelAll(sameValue), Is.EqualTo(1));
            Assert.That(handle.IsActive(), Is.False);
        }

        [Test]
        public void Test_MotionInSequence_IsSkipped()
        {
            var target = new object();
            var child = Create(1f).RunWithoutBinding().SetTarget(target);
            var sequence = LSequence.Create().Append(child).Run(b => b.WithScheduler(dispatcher.Scheduler));

            Assert.That(LMotion.CancelAll(target), Is.EqualTo(0));
            Assert.That(LMotion.CompleteAll(target), Is.EqualTo(0));
            Assert.That(sequence.IsActive(), Is.True);

            // Registering the sequence itself works.
            sequence.SetTarget(target);
            Assert.That(LMotion.CancelAll(target), Is.EqualTo(1));
            Assert.That(sequence.IsActive(), Is.False);
            Assert.That(child.IsActive(), Is.False);

            // A motion that is already in a sequence cannot be registered.
            var child2 = Create(1f).RunWithoutBinding();
            LSequence.Create().Append(child2).Run(b => b.WithScheduler(dispatcher.Scheduler));
            Assert.Throws<InvalidOperationException>(() => child2.SetTarget(target));
        }

        [Test]
        public void Test_RegistrationInsideCallback_IsNotAffected()
        {
            var target = new object();
            var newer = default(MotionHandle);
            Create(1f)
                .WithOnCancel(() => newer = Create(1f).RunWithoutBinding().SetTarget(target))
                .RunWithoutBinding()
                .SetTarget(target);

            Assert.That(LMotion.CancelAll(target), Is.EqualTo(1));
            Assert.That(newer.IsActive(), Is.True);
            Assert.That(MotionTargetRegistry.GetEntryCount(target), Is.EqualTo(1));
            Assert.That(LMotion.CancelAll(target), Is.EqualTo(1));
            Assert.That(newer.IsActive(), Is.False);
        }

        [Test]
        public void Test_StaleHandleAfterReset_DoesNotAffectNewMotion()
        {
            var target = new object();
            var old = Create(1f).RunWithoutBinding().SetTarget(target);

            // Resetting the dispatcher discards the motion without callbacks and reuses its slot.
            dispatcher.Reset();
            var unrelated = Create(1f).RunWithoutBinding();
            Assert.That(unrelated, Is.EqualTo(old), "The new motion is expected to reuse the handle of the discarded one");

            Assert.That(LMotion.CancelAll(target), Is.EqualTo(0));
            Assert.That(unrelated.IsActive(), Is.True);
        }

        [Test]
        public void Test_CallbackResetsTheDispatcher_RegistrationsMadeAfterwardsAreNotAffected()
        {
            var target = new object();
            var replacement = default(MotionHandle);
            Create(1f).WithOnCancel(() =>
            {
                dispatcher.Reset();
                Create(1f).RunWithoutBinding();
                replacement = Create(1f).RunWithoutBinding().SetTarget(target);
            }).RunWithoutBinding().SetTarget(target);
            var sibling = Create(1f).RunWithoutBinding().SetTarget(target);

            var canceled = LMotion.CancelAll(target);

            // The reset discarded `sibling` and the replacement reuses its handle, with the same target:
            // the registration identity tells them apart. (Resetting from a callback is not supported: the canceled motion
            // no longer exists when the call is done with it, so it is not counted.)
            Assert.That(replacement, Is.EqualTo(sibling));
            Assert.That(canceled, Is.Zero);
            Assert.That(replacement.IsActive(), Is.True);
            Assert.That(LMotion.CancelAll(target), Is.EqualTo(1));
        }

        [Test]
        public void Test_RegistrationIsReleasedWhenTheMotionIsRemoved()
        {
            var target = new object();
            var completed = Create(0.5f).RunWithoutBinding().SetTarget(target);
            var canceled = Create(5f).RunWithoutBinding().SetTarget(target);

            dispatcher.Update(0.6); // `completed` completes
            canceled.Cancel();
            Assert.That(MotionTargetRegistry.GetEntryCount(target), Is.EqualTo(2));

            dispatcher.Update(0.1); // both are removed in this update
            Assert.That(completed.IsValid(), Is.False);
            Assert.That(canceled.IsValid(), Is.False);
            Assert.That(MotionTargetRegistry.GetEntryCount(target), Is.Zero);
            Assert.That(MotionTargetRegistry.TargetCount, Is.Zero, "The registry must not keep a reference to the target");
        }

        [Test]
        public void Test_RegistrationOfARemovedSequenceChildIsReleased()
        {
            var target = new object();
            var child = Create(1f).RunWithoutBinding().SetTarget(target);
            var sequence = LSequence.Create().Append(child).Run(b => b.WithScheduler(dispatcher.Scheduler));
            sequence.Cancel();

            dispatcher.Update(0.25); // the child is removed in this update

            Assert.That(child.IsValid(), Is.False);
            Assert.That(MotionTargetRegistry.GetEntryCount(target), Is.Zero);
            Assert.That(MotionTargetRegistry.TargetCount, Is.Zero);
        }

        [Test]
        public void Test_RegistrationsAreReleasedWhenTheDispatcherIsReset()
        {
            var target = new object();
            Create(1f).RunWithoutBinding().SetTarget(target);
            Create(1f).RunWithoutBinding().SetTarget(target);

            dispatcher.Reset();

            Assert.That(MotionTargetRegistry.GetEntryCount(target), Is.Zero);
            Assert.That(MotionTargetRegistry.TargetCount, Is.Zero);
        }

        // A callback of CancelAll moves B to another target and back: that is a new registration, made after the call started,
        // so this call does not cancel B (like any registration made by its callbacks).
        [Test]
        public void Test_RetargetedAwayAndBackInACallback_IsANewRegistration()
        {
            var target = new object();
            var other = new object();
            var b = default(MotionHandle);
            Create(1f).WithOnCancel(() => b.SetTarget(other).SetTarget(target)).RunWithoutBinding().SetTarget(target);
            b = Create(1f).RunWithoutBinding().SetTarget(target);

            Assert.That(LMotion.CancelAll(target), Is.EqualTo(1));
            Assert.That(b.IsActive(), Is.True);

            Assert.That(LMotion.CancelAll(target), Is.EqualTo(1));
            Assert.That(b.IsActive(), Is.False);
        }

        // Registering again to the same target is a new registration as well.
        [Test]
        public void Test_RegisteredAgainToTheSameTargetInACallback_IsANewRegistration()
        {
            var target = new object();
            var b = default(MotionHandle);
            Create(1f).WithOnCancel(() => b.SetTarget(target)).RunWithoutBinding().SetTarget(target);
            b = Create(1f).RunWithoutBinding().SetTarget(target);

            Assert.That(LMotion.CancelAll(target), Is.EqualTo(1));
            Assert.That(b.IsActive(), Is.True);
            Assert.That(MotionTargetRegistry.GetEntryCount(target), Is.EqualTo(1));

            Assert.That(LMotion.CancelAll(target), Is.EqualTo(1));
            Assert.That(b.IsActive(), Is.False);
        }

        // The registrations that survive a call (here a completed preserved motion) keep their place before the
        // registrations made by the callbacks of that call.
        [Test]
        public void Test_SurvivingRegistrationsKeepTheirOrder()
        {
            var target = new object();
            var canceled = new List<int>();
            var first = Create(1f).WithOnCancel(() => canceled.Add(1)).RunWithoutBinding().Preserve().SetTarget(target);
            first.Complete();
            Create(1f).WithOnComplete(() =>
            {
                Create(1f).WithOnCancel(() => canceled.Add(2)).RunWithoutBinding().SetTarget(target);
            }).RunWithoutBinding().SetTarget(target);

            Assert.That(LMotion.CompleteAll(target), Is.EqualTo(1));
            Assert.That(LMotion.CancelAll(target), Is.EqualTo(2));
            Assert.That(canceled, Is.EqualTo(new[] { 1, 2 }));
        }

        // CompleteAll counts the motions it completed: a motion whose OnStart moves it elsewhere is not completed.
        [Test]
        public void Test_CompleteAll_DoesNotCountAMotionThatOnStartMovedElsewhere()
        {
            var target = new object();
            var completes = 0;
            var handle = default(MotionHandle);
            handle = Create(1f)
                .WithOnStart(() => handle.Time = 0.25)
                .WithOnComplete(() => completes++)
                .RunWithoutBinding().SetTarget(target);

            Assert.That(LMotion.CompleteAll(target), Is.Zero);
            Assert.That(completes, Is.Zero);
            Assert.That(handle.IsActive(), Is.True);
            Assert.That(handle.Time, Is.EqualTo(0.25));
            Assert.That(handle.TryComplete(), Is.True);
        }

        // CompleteAll counts the motions that are completed when it is done with them. A motion moved back by its own bind
        // (no fork feature: it is dispatched as in v2.0.2) is not completed.
        [Test]
        public void Test_CompleteAll_DoesNotCountAMotionMovedBackByItsBind()
        {
            var target = new object();
            var writes = new List<float>();
            var handle = default(MotionHandle);
            handle = Create(1f).WithImmediateBind(false).Bind(x =>
            {
                writes.Add(x);
                if (x == 1f) handle.Time = 0.25;
            }).SetTarget(target);

            Assert.That(LMotion.CompleteAll(target), Is.Zero);
            Assert.That(writes, Is.EqualTo(new[] { 1f, 0.25f }));
            Assert.That(handle.IsActive(), Is.True);
        }

        // OnStart completes the motion itself (nested Complete()): it is completed when the call is done with it, so CompleteAll
        // counts it, while the outer TryComplete() of the guarded handle was superseded and returns false. OnComplete is invoked once.
        [TestCase(true)]
        [TestCase(false)]
        public void Test_OnStartCompletesTheMotionItself_CompleteAllCountsIt_OuterTryCompleteReturnsFalse(bool throughCompleteAll)
        {
            var target = new object();
            var completes = 0;
            var handle = default(MotionHandle);
            handle = Create(1f)
                .WithOnStart(() => handle.Complete())
                .WithOnComplete(() => completes++)
                .RunWithoutBinding().SetTarget(target);

            if (throughCompleteAll) Assert.That(LMotion.CompleteAll(target), Is.EqualTo(1));
            else Assert.That(handle.TryComplete(), Is.False);

            Assert.That(completes, Is.EqualTo(1));
            Assert.That(handle.IsActive(), Is.False);
        }

        // A sequence reopened by a child callback while it releases its children is not completed: it is not counted, and the
        // OnComplete of the user is not invoked (the release that precedes it was superseded).
        [Test]
        public void Test_CompleteAll_DoesNotCountASequenceReopenedWhileReleasingItsChildren()
        {
            var target = new object();
            var completes = 0;
            var parent = default(MotionHandle);
            var first = Create(1f).WithOnCancel(() => parent.Time = 0.25).RunWithoutBinding();
            var second = Create(1f).RunWithoutBinding();
            parent = LSequence.Create()
                .Append(first)
                .Join(second)
                .AppendCallback(static () => { })
                .Run(b => b.WithScheduler(dispatcher.Scheduler).WithOnComplete(() => completes++))
                .SetTarget(target);

            Assert.That(LMotion.CompleteAll(target), Is.Zero);
            Assert.That(completes, Is.Zero);
            Assert.That(parent.IsActive(), Is.True);
            Assert.That(second.IsActive(), Is.True);
            Assert.That(parent.Time, Is.EqualTo(0.25));
        }

        sealed class MutableKey
        {
            public int Hash;
            public override int GetHashCode() => Hash;
        }

        [Test]
        public void Test_KeyWhoseHashChanged_DoesNotBreakOtherRegistrations()
        {
            var key = new MutableKey { Hash = 17 };
            var mutated = Create(1f).RunWithoutBinding().SetTarget(key);
            key.Hash = 123;

            // Enough new targets to trigger a sweep, while the mutated key is still registered.
            Assert.DoesNotThrow(() =>
            {
                for (var i = 0; i < 200; i++) Create(1f).RunWithoutBinding().SetTarget(new object());
            });

            // The sweep rehashed the key, so it is found again.
            Assert.That(LMotion.CancelAll(key), Is.EqualTo(1));
            Assert.That(mutated.IsActive(), Is.False);
        }

        [Test]
        public void Test_InvalidArguments()
        {
            var handle = Create(1f).RunWithoutBinding();
            Assert.Throws<ArgumentNullException>(() => handle.SetTarget(null));
            Assert.Throws<ArgumentException>(() => handle.SetTarget(handle));
            Assert.Throws<ArgumentNullException>(() => LMotion.CancelAll(null));
            Assert.Throws<ArgumentNullException>(() => LMotion.CompleteAll(null));
            Assert.Throws<ArgumentException>(() => LMotion.CancelAll(handle));

            handle.Cancel();
            Assert.Throws<InvalidOperationException>(() => handle.SetTarget(new object()));
            dispatcher.Update(0.1);
            Assert.Throws<InvalidOperationException>(() => handle.SetTarget(new object()));
            Assert.That(LMotion.CancelAll(new object()), Is.EqualTo(0));
        }

        [Test]
        public void Test_PruneOnEnterPlayMode_KeepsLiveMotions()
        {
            var live = Create(1f).RunWithoutBinding().SetTarget("live");
            var gone = Create(1f).RunWithoutBinding().SetTarget("gone");
            gone.Complete(); // completed, not removed yet (no runner update): its entry is stale
            Assert.That(MotionTargetRegistry.TargetCount, Is.EqualTo(2));

            var method = typeof(MotionTargetRegistry).GetMethod("PruneAfterEnterPlayMode", BindingFlags.NonPublic | BindingFlags.Static);
            var attribute = method.GetCustomAttribute<RuntimeInitializeOnLoadMethodAttribute>();
            Assert.That(attribute, Is.Not.Null);
            Assert.That(attribute.loadType, Is.EqualTo(RuntimeInitializeLoadType.AfterSceneLoad));

            method.Invoke(null, null);
            Assert.That(MotionTargetRegistry.TargetCount, Is.EqualTo(1));
            Assert.That(MotionTargetRegistry.GetEntryCount("live"), Is.EqualTo(1));
            Assert.That(LMotion.CancelAll("live"), Is.EqualTo(1));
            Assert.That(live.IsActive(), Is.False);
        }

        [UnityTest]
        public IEnumerator Test_PlayerLoop()
        {
            var target = new GameObject("Target");
            var handle = LMotion.Create(0f, 1f, 0.2f).RunWithoutBinding().SetTarget(target.transform);
            yield return handle.ToYieldInstruction();
            Assert.That(LMotion.CancelAll(target.transform), Is.EqualTo(0));

            handle = LMotion.Create(0f, 1f, 1f).RunWithoutBinding().SetTarget(target.transform);
            yield return null;
            Assert.That(LMotion.CancelAll(target.transform), Is.EqualTo(1));
            Object.Destroy(target);
        }

        [Test]
        public void Test_NoAllocation()
        {
            var target = new object();
            var transform = new GameObject("Target").transform;
            TestDelegate run = () =>
            {
                Create(1f).RunWithoutBinding().SetTarget(target);
                Create(1f).RunWithoutBinding().SetTarget(target);
                LMotion.CancelAll(target);
                Create(1f).RunWithoutBinding().SetTarget(target);
                LMotion.CompleteAll(target);
                Create(1f).RunWithoutBinding().SetTarget(transform);
                Create(1f).RunWithoutBinding().SetTarget("id");
                LMotion.CancelAll(transform);
                LMotion.CancelAll("id");
            };

            for (int i = 0; i < 3; i++)
            {
                run();
                dispatcher.Update(0.1);
            }

            Assert.That(run, AllocIs.Not.AllocatingGCMemory());
            Object.DestroyImmediate(transform.gameObject);
        }

        [Test]
        public void Test_NoAllocation_NewTargetsOnceTheTablesHaveGrown()
        {
            var targets = new object[129];
            var moreTargets = new object[129];
            for (int i = 0; i < targets.Length; i++)
            {
                targets[i] = new object();
                moreTargets[i] = new object();
            }
            dispatcher.EnsureStorageCapacity<float, NoOptions, Adapters.FloatMotionAdapter>(512);

            // First use: the tables grow (amortized allocations).
            foreach (var target in targets) Create(1f).RunWithoutBinding().SetTarget(target);
            foreach (var target in targets) LMotion.CancelAll(target);
            dispatcher.Update(0.1);

            // Targets never seen before reuse the capacity.
            Assert.That(() =>
            {
                foreach (var target in moreTargets) Create(1f).RunWithoutBinding().SetTarget(target);
            }, AllocIs.Not.AllocatingGCMemory());
            Assert.That(MotionTargetRegistry.TargetCount, Is.EqualTo(moreTargets.Length));
        }

        [Test]
        public void Test_ValueTypeTarget_IsBoxed()
        {
            // Documented: a value type target is boxed on every call.
            for (int i = 0; i < 3; i++)
            {
                Create(1f).RunWithoutBinding().SetTarget(1234);
                LMotion.CancelAll(1234);
                dispatcher.Update(0.1);
            }

            Assert.That(() =>
            {
                Create(1f).RunWithoutBinding().SetTarget(1234);
                LMotion.CancelAll(1234);
            }, AllocIs.AllocatingGCMemory());
        }
    }
}
