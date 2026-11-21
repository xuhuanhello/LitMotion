using System.Collections.Generic;
using LitMotion.Adapters;
using LitMotion.Extensions;
using NUnit.Framework;
using UnityEngine;
using Is = NUnit.Framework.Is;

namespace LitMotion.Tests.Runtime
{
    // A callback of an extension (OnStart, sequence callback) may move, complete or cancel motions, create motions
    // (which can reallocate the storage), or cancel the sequence that is being evaluated. After the callback, the
    // evaluation in progress must not bind a stale value, invoke further callbacks, complete or release anything
    // that the callback has superseded.
    public class ReentrancyTest
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

        MotionBuilder<float, NoOptions, FloatMotionAdapter> Create(float duration = 1f)
        {
            return LMotion.Create(0f, 1f, duration).WithScheduler(dispatcher.Scheduler).WithImmediateBind(false);
        }

        // ---- OnStart ----

        [TestCase(false)]
        [TestCase(true)]
        public void Test_OnStartSetsTime_TheNewValueIsKept(bool startThroughTime)
        {
            var values = new List<float>();
            var handle = default(MotionHandle);
            handle = Create().WithOnStart(() => handle.Time = 0.75).Bind(values.Add);

            if (startThroughTime) handle.Time = 0.25;
            else dispatcher.Update(0.25);

            Assert.That(handle.Time, Is.EqualTo(0.75));
            Assert.That(values, Is.EqualTo(new[] { 0.75f }));
        }

        [Test]
        public void Test_OnStartSetsTime_CanvasGroupKeepsTheNewValue()
        {
            var gameObject = new GameObject("Target", typeof(CanvasGroup));
            try
            {
                var group = gameObject.GetComponent<CanvasGroup>();
                var handle = default(MotionHandle);
                handle = LMotion.Create(0f, 1f, 1f).WithScheduler(dispatcher.Scheduler)
                    .WithOnStart(() => handle.Time = 0.75)
                    .BindToAlpha(group);

                dispatcher.Update(0.25);

                Assert.That(group.alpha, Is.EqualTo(0.75f));
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Test_OnStartCancelsTheMotion_DuringComplete_CancelWins(bool preserve)
        {
            var events = new List<string>();
            var handle = default(MotionHandle);
            handle = Create().WithDelay(1f)
                .WithOnStart(() => events.Add($"try-cancel={handle.TryCancel()}"))
                .WithOnCancel(() => events.Add("cancel"))
                .WithOnComplete(() => events.Add("complete"))
                .Bind(value => events.Add($"value={value}"));
            if (preserve) handle.Preserve();

            Assert.DoesNotThrow(() => handle.Complete());
            Assert.That(events, Is.EqualTo(new[] { "cancel", "try-cancel=True" }));
            Assert.That(handle.IsActive(), Is.False);
        }

        [Test]
        public void Test_OnStartGrowsTheStorageThenCancelsItself_NoValueIsBound()
        {
            var values = new List<float>();
            var handle = default(MotionHandle);
            handle = Create().WithOnStart(() =>
            {
                for (int i = 0; i < 64; i++) Create().RunWithoutBinding();
                handle.Cancel();
            }).Bind(values.Add);

            dispatcher.Update(0.25);

            Assert.That(handle.IsActive(), Is.False);
            Assert.That(values, Is.Empty);
        }

        [Test]
        public void Test_OnStartGrowsTheStorageAndCancelsALaterMotion_ItsOnStartIsNotInvoked()
        {
            var started = 0;
            var sibling = default(MotionHandle);
            Create().WithOnStart(() =>
            {
                for (int i = 0; i < 64; i++) Create().RunWithoutBinding();
                sibling.Cancel();
            }).RunWithoutBinding();
            sibling = Create().WithOnStart(() => started++).RunWithoutBinding();

            dispatcher.Update(0.25);

            Assert.That(sibling.IsActive(), Is.False);
            Assert.That(started, Is.Zero);
        }

        [Test]
        public void Test_OnStartCompletesTheMotion_OnCompleteIsInvokedOnce()
        {
            var completed = 0;
            var handle = default(MotionHandle);
            handle = Create().WithOnStart(() => handle.Complete()).WithOnComplete(() => completed++).RunWithoutBinding();

            dispatcher.Update(0.25);
            dispatcher.Update(0.25);

            Assert.That(completed, Is.EqualTo(1));
        }

        // The first evaluation reaches the end: while OnStart runs, the motion has started but is not completed yet,
        // so it can be canceled from OnStart through any entry (as with Complete()).
        [TestCase("Runner")]
        [TestCase("SetTime")]
        [TestCase("Complete")]
        public void Test_OnStartCancelsTheMotion_OnAnEvaluationThatReachesTheEnd(string entry)
        {
            var events = new List<string>();
            var handle = default(MotionHandle);
            handle = Create()
                .WithOnStart(() =>
                {
                    events.Add("active=" + handle.IsActive());
                    events.Add("try-cancel=" + handle.TryCancel());
                })
                .WithOnComplete(() => events.Add("complete"))
                .WithOnCancel(() => events.Add("cancel"))
                .Bind(x => events.Add("value=" + x));

            if (entry == "Runner") dispatcher.Update(1.125);
            else if (entry == "SetTime") handle.Time = 1.125;
            else handle.Complete();

            Assert.That(events, Is.EqualTo(new[] { "active=True", "cancel", "try-cancel=True" }));
            Assert.That(handle.IsActive(), Is.False);
        }

        // Each WithOnStart() call registers one callback; the evaluation checks that it is still current after each of them.
        [TestCase(false)]
        [TestCase(true)]
        public void Test_FirstOnStartCancelsTheMotion_LaterOnStartCallbacksAreNotInvoked(bool throughComplete)
        {
            var events = new List<string>();
            var handle = default(MotionHandle);
            handle = Create()
                .WithOnStart(() => { events.Add("first"); handle.Cancel(); })
                .WithOnStart(() => events.Add("second"))
                .WithOnCancel(() => events.Add("cancel"))
                .WithOnComplete(() => events.Add("complete"))
                .Bind(_ => events.Add("write"));

            if (throughComplete) handle.Complete();
            else dispatcher.Update(0.25);

            Assert.That(events, Is.EqualTo(new[] { "first", "cancel" }));
        }

        [Test]
        public void Test_FirstOnStartThrows_LaterOnStartCallbacksAreStillInvoked()
        {
            var events = new List<string>();
            var errors = new List<string>();
            var saved = MotionDispatcher.GetUnhandledExceptionHandler();
            MotionDispatcher.RegisterUnhandledExceptionHandler(ex => errors.Add(ex.Message));
            try
            {
                Create()
                    .WithOnStart(() => { events.Add("first"); throw new System.InvalidOperationException("first"); })
                    .WithOnStart(() => events.Add("second"))
                    .Bind(_ => events.Add("write"));

                dispatcher.Update(0.25);
            }
            finally
            {
                MotionDispatcher.RegisterUnhandledExceptionHandler(saved);
            }

            Assert.That(events, Is.EqualTo(new[] { "first", "second", "write" }));
            Assert.That(errors, Is.EqualTo(new[] { "first" }));
        }

        // ---- identity of an evaluation ----

        static readonly System.Reflection.FieldInfo LastEvaluationVersion = typeof(MotionEvaluation)
            .GetField("lastVersion", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        // The dispatcher is reset inside OnStart and a new motion takes the same slot (same handle). Even if it gets the same
        // evaluation version (forced here, as after a wrap-around of the counter), the old evaluation must not write to it.
        [Test]
        public void Test_ResetInOnStart_NewMotionInTheSameSlot_IsNotWrittenByTheOldEvaluation()
        {
            var saved = (uint)LastEvaluationVersion.GetValue(null);
            try
            {
                var oldWrites = 0;
                var newWrites = 0;
                var replacement = default(MotionHandle);
                var old = default(MotionHandle);
                old = Create().WithOnStart(() =>
                {
                    var version = MotionManager.GetDataRef(old, false).State.EvaluationVersion;
                    dispatcher.Reset();
                    LastEvaluationVersion.SetValue(null, version - 1);
                    replacement = Create().WithOnStart(static () => { }).Bind(_ => newWrites++);
                }).Bind(_ => oldWrites++);

                old.Time = 0.25;

                Assert.That(replacement, Is.EqualTo(old));
                Assert.That(oldWrites, Is.Zero);
                Assert.That(newWrites, Is.Zero);
                Assert.That(replacement.IsActive(), Is.True);
            }
            finally
            {
                LastEvaluationVersion.SetValue(null, saved);
            }
        }

        // Evaluations of motions of different storages never supersede each other, even with equal versions.
        [Test]
        public void Test_MotionsOfTwoStoragesWithTheSameVersion_DoNotSupersedeEachOther()
        {
            var saved = (uint)LastEvaluationVersion.GetValue(null);
            var other = new ManualMotionDispatcher();
            try
            {
                var aWrites = 0;
                var bWrites = 0;
                var b = default(MotionHandle);
                LastEvaluationVersion.SetValue(null, 100u);
                var a = Create().WithOnStart(() => b.Time = 0.25).Bind(_ => aWrites++);
                LastEvaluationVersion.SetValue(null, 100u);
                b = LMotion.Create(0f, 1f, 1f).WithScheduler(other.Scheduler).WithImmediateBind(false)
                    .WithOnStart(static () => { }).Bind(_ => bWrites++);

                a.Time = 0.25;

                Assert.That(aWrites, Is.EqualTo(1));
                Assert.That(bWrites, Is.EqualTo(1));
            }
            finally
            {
                other.Reset();
                LastEvaluationVersion.SetValue(null, saved);
            }
        }

        // ---- motions of the same runner batch ----

        // A's OnStart re-evaluates B before the runner reaches B: the output the job computed for B is stale and must not be
        // bound, and B's loop/complete events must not be dispatched a second time.
        [TestCase(false)]
        [TestCase(true)]
        public void Test_EarlierOnStartReevaluatesALaterMotionOfTheBatch_ItsJobOutputIsDropped(bool complete)
        {
            var writes = new List<float>();
            var loops = 0;
            var completes = 0;
            var later = default(MotionHandle);
            Create().WithOnStart(() =>
            {
                if (complete) later.Complete();
                else later.Time = 0.75;
            }).RunWithoutBinding();
            later = Create().WithOnStart(static () => { })
                .WithOnLoopComplete(_ => loops++)
                .WithOnComplete(() => completes++)
                .Bind(writes.Add);

            dispatcher.Update(0.25);

            Assert.That(writes, Is.EqualTo(new[] { complete ? 1f : 0.75f }));
            Assert.That(loops, Is.EqualTo(complete ? 1 : 0));
            Assert.That(completes, Is.EqualTo(complete ? 1 : 0));
        }

        // The publication of a job output must not depend on the control flags (cleared by assigning PlaybackSpeed) nor on
        // arrays read before the callbacks (reallocated when a callback grows the storage).
        [TestCase(false)]
        [TestCase(true)]
        public void Test_EarlierOnStartStartsPlayingALaterMotionBackwardsAndSeeksIt_ItsJobOutputIsDropped(bool growStorage)
        {
            var writes = new List<float>();
            var later = default(MotionHandle);
            Create().WithOnStart(() =>
            {
                if (growStorage) dispatcher.EnsureStorageCapacity<float, NoOptions, FloatMotionAdapter>(128);
                later.PlayBackwards();
                later.Time = 0.75;
            }).RunWithoutBinding();
            later = Create().Bind(writes.Add);

            dispatcher.Update(0.25);

            Assert.That(writes, Is.EqualTo(new[] { 0.75f }));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Test_EarlierOnStartEndsTheBackwardsModeOfALaterMotionAndSeeksIt_ItsJobOutputIsDropped(bool growStorage)
        {
            var writes = new List<float>();
            var later = default(MotionHandle);
            Create().WithOnStart(() =>
            {
                if (growStorage) dispatcher.EnsureStorageCapacity<float, NoOptions, FloatMotionAdapter>(128);
                later.PlaybackSpeed = 1f;
                later.Time = 0.625;
            }).RunWithoutBinding();
            later = Create().Bind(writes.Add);
            later.Time = 0.75;
            later.PlayBackwards();
            writes.Clear();

            dispatcher.Update(0.25);

            Assert.That(writes, Is.EqualTo(new[] { 0.625f }));
        }

        // B was canceled, so the job of this batch marks it for removal. A's OnStart moves B before the end of the batch:
        // that evaluation supersedes the removal, B stays alive.
        [Test]
        public void Test_EarlierOnStartMovesALaterMotionMarkedForRemoval_ItIsNotRemoved()
        {
            var writes = new List<float>();
            var activeInCallback = false;
            var later = default(MotionHandle);
            Create().WithOnStart(() =>
            {
                later.Time = 0.75;
                activeInCallback = later.IsActive();
            }).RunWithoutBinding();
            later = Create().WithOnStart(static () => { }).Bind(writes.Add);
            later.Cancel();

            dispatcher.Update(0.25);

            Assert.That(activeInCallback, Is.True);
            Assert.That(writes, Is.EqualTo(new[] { 0.75f }));
            Assert.That(later.IsActive(), Is.True);
            dispatcher.Update(0.125);
            Assert.That(writes, Is.EqualTo(new[] { 0.75f, 0.875f }));
        }

        // ---- motions created during an update ----

        [Test]
        public void Test_MotionCreatedInOnStart_IsNotEvaluatedWithTheOutputOfTheCurrentBatch()
        {
            var values = new List<float>();
            var created = default(MotionHandle);
            Create().WithOnStart(() =>
            {
                created = LMotion.Create(10f, 20f, 1f).WithScheduler(dispatcher.Scheduler).WithImmediateBind(false).Bind(values.Add);
                created.PlayBackwards();
            }).RunWithoutBinding();

            dispatcher.Update(0.25);

            Assert.That(values, Is.EqualTo(new[] { 10f }));
        }

        // The same situation without any extension. In v2.0.2 the runner also visits the motions created during the
        // update and binds them with a value read past the end of the job output (undefined). The runner now only
        // visits the motions of the job batch: this is the one intended difference for code that does not use extensions.
        [Test]
        public void Test_MotionCreatedInBind_IsNotEvaluatedWithTheOutputOfTheCurrentBatch()
        {
            var writes = 0;
            var created = default(MotionHandle);
            var first = true;
            Create().Bind(_ =>
            {
                if (!first) return;
                first = false;
                created = LMotion.Create(10f, 20f, 1f).WithScheduler(dispatcher.Scheduler).WithImmediateBind(false).Bind(_ => writes++);
                created.Time = 0;
            });

            dispatcher.Update(0.25);

            Assert.That(writes, Is.EqualTo(1));
        }

        // ---- sequences ----

        [TestCase(false)]
        [TestCase(true)]
        public void Test_SequenceCallbackSeeksBackFromTheLastFrame_CompletionIsDropped(bool explicitComplete)
        {
            var completed = 0;
            var seeks = 0;
            var child = Create().RunWithoutBinding();
            var sequence = default(MotionHandle);
            sequence = LSequence.Create()
                .Append(child)
                .InsertCallback(0.5f, () =>
                {
                    if (seeks++ == 0) sequence.Time = 0.25;
                })
                .Run(b => b.WithScheduler(dispatcher.Scheduler).WithOnComplete(() => completed++));

            if (explicitComplete) sequence.Complete();
            else dispatcher.Update(1.25);

            Assert.That(sequence.Time, Is.EqualTo(0.25));
            Assert.That(sequence.IsActive(), Is.True);
            Assert.That(child.IsActive(), Is.True);
            Assert.That(completed, Is.Zero);

            dispatcher.Update(1.0);
            Assert.That(completed, Is.EqualTo(1));
        }

        // The sequence is guarded because of its own callback, or only because a child uses OnStart.
        [TestCase(true)]
        [TestCase(false)]
        public void Test_ChildOnStartCancelsTheSequence_EarlierChildIsNotReactivated(bool sequenceHasCallback)
        {
            // The later child starts exactly at the new time: it is evaluated after the earlier one (README 1.1), whose value is
            // written once before the cancel and never after it.
            var parent = default(MotionHandle);
            var events = new List<string>();
            var earlier = Create(0.5f).Bind(_ => events.Add("earlier"));
            var later = Create(0.5f).WithOnStart(() => { events.Add("cancel"); parent.Cancel(); }).RunWithoutBinding();
            var builder = LSequence.Create()
                .Insert(0.25f, earlier)
                .Insert(0.5f, later);
            if (sequenceHasCallback) builder.AppendCallback(() => { });
            parent = builder.Run(b => b.WithScheduler(dispatcher.Scheduler));

            parent.Time = 0.5;

            Assert.That(parent.IsActive(), Is.False);
            Assert.That(earlier.IsActive(), Is.False);
            Assert.That(events, Is.EqualTo(new[] { "earlier", "cancel" }));
        }

        // The sequence uses only OnStart itself (no sequence callback, no child using a fork feature): a child that cancels the
        // sequence still stops the evaluation of the other children.
        [Test]
        public void Test_ChildCancelsASequenceThatOnlyUsesOnStart_LaterChildIsNotWritten()
        {
            var parent = default(MotionHandle);
            var laterWrites = new List<float>();
            var first = Create().Bind(_ => parent.Cancel());
            var later = Create().Bind(laterWrites.Add);
            parent = LSequence.Create()
                .Append(first)
                .Insert(0.125f, later)
                .Run(b => b.WithScheduler(dispatcher.Scheduler).WithOnStart(static () => { }));

            parent.Time = 0.5;

            Assert.That(parent.IsActive(), Is.False);
            Assert.That(later.IsActive(), Is.False);
            Assert.That(laterWrites, Is.Empty);
        }

        // The OnComplete/OnCancel of the user runs after the sequence was released to the pool. A new sequence created there
        // may reuse the same internal object: the old handle must not drive it.
        [TestCase(false)]
        [TestCase(true)]
        public void Test_OldSequenceHandleAfterRelease_DoesNotDriveTheSequenceThatReusesItsObject(bool cancel)
        {
            var childWrites = new List<float>();
            var old = default(MotionHandle);
            var replacement = default(MotionHandle);
            void Terminal()
            {
                var child = Create().Bind(childWrites.Add);
                replacement = LSequence.Create().Append(child).AppendCallback(static () => { })
                    .Run(b => b.WithScheduler(dispatcher.Scheduler));
                old.Time = 0.25;
            }

            old = LSequence.Create().AppendInterval(1f).AppendCallback(static () => { }).Run(b =>
            {
                b.WithScheduler(dispatcher.Scheduler);
                if (cancel) b.WithOnCancel(Terminal);
                else b.WithOnComplete(Terminal);
            });
            if (cancel) old.Cancel();
            else old.Complete();

            Assert.That(replacement.Time, Is.Zero);
            Assert.That(childWrites, Is.Empty);
            dispatcher.Update(0.5);
            Assert.That(childWrites, Is.EqualTo(new[] { 0.5f }));
        }

        // The update that completes the sequence invokes its last callbacks before the completion is published: a callback can
        // still cancel the sequence, which ends the update (no later callback, no OnComplete). Same for the three entries.
        [TestCase("Runner")]
        [TestCase("SetTime")]
        [TestCase("Complete")]
        public void Test_SequenceCallbackCancelsTheSequenceInTheUpdateThatCompletesIt(string entry)
        {
            var events = new List<string>();
            var sequence = default(MotionHandle);
            sequence = LSequence.Create()
                .AppendInterval(1f)
                .InsertCallback(0.5f, () => events.Add("try-cancel:" + sequence.TryCancel()))
                .AppendCallback(() => events.Add("tail"))
                .Run(b => b.WithScheduler(dispatcher.Scheduler)
                    .WithOnCancel(() => events.Add("cancel"))
                    .WithOnComplete(() => events.Add("complete")));

            if (entry == "Runner") dispatcher.Update(1.125);
            else if (entry == "SetTime") sequence.Time = 1.125;
            else sequence.Complete();

            Assert.That(events, Is.EqualTo(new[] { "cancel", "try-cancel:True" }));
            Assert.That(sequence.IsActive(), Is.False);
        }

        // While a sequence completes, it cancels its children. A child's OnCancel that moves the sequence back reopens it:
        // the completion stops there (the other children are not canceled and the sequence is not released).
        [TestCase(false)]
        [TestCase(true)]
        public void Test_ChildOnCancelReopensTheCompletingSequence_TheSequenceKeepsItsChildren(bool explicitComplete)
        {
            var reopened = 0;
            var parent = default(MotionHandle);
            var first = Create().WithOnCancel(() =>
            {
                if (reopened++ == 0) parent.Time = 0.25;
            }).RunWithoutBinding();
            var second = Create().RunWithoutBinding();
            parent = LSequence.Create()
                .Append(first)
                .Join(second)
                .AppendCallback(() => { })
                .Run(b => b.WithScheduler(dispatcher.Scheduler));

            if (explicitComplete) parent.Complete();
            else dispatcher.Update(1.25);

            Assert.That(reopened, Is.EqualTo(1));
            Assert.That(parent.IsActive(), Is.True);
            Assert.That(parent.Time, Is.EqualTo(0.25));
            Assert.That(second.IsActive(), Is.True);

            // The sequence still works: it completes normally.
            var completes = 0;
            dispatcher.Update(1.0);
            Assert.That(parent.IsActive(), Is.False);
            Assert.That(second.IsActive(), Is.False);
        }

        [Test]
        public void Test_ChildCancelsTheSequenceAndANewSequenceReusesIt_TheNewOneIsNotWritten()
        {
            var newChildWrites = 0;
            var replacement = default(MotionHandle);
            var sequence = default(MotionHandle);
            var later = Create(0.5f).WithOnStart(() =>
            {
                sequence.Cancel();
                var newChild = Create().Bind(_ => newChildWrites++);
                replacement = LSequence.Create().Append(newChild).Append(Create().RunWithoutBinding())
                    .Run(b => b.WithScheduler(dispatcher.Scheduler));
            }).RunWithoutBinding();
            sequence = LSequence.Create()
                .Insert(0.25f, Create(0.5f).RunWithoutBinding())
                .Insert(0.5f, later)
                .AppendCallback(() => { })
                .Run(b => b.WithScheduler(dispatcher.Scheduler));

            sequence.Time = 0.5;

            Assert.That(replacement.IsActive(), Is.True);
            Assert.That(newChildWrites, Is.Zero);
        }

        [Test]
        public void Test_CallbackSeeksForward_IntermediateCallbacksAreKept()
        {
            var hits = new List<int>();
            var sequence = default(MotionHandle);
            sequence = LSequence.Create()
                .AppendInterval(1f)
                .InsertCallback(0.25f, () =>
                {
                    hits.Add(1);
                    sequence.Time = 0.625;
                })
                .InsertCallback(0.5f, () => hits.Add(2))
                .InsertCallback(0.75f, () => hits.Add(3))
                .Run(b => b.WithScheduler(dispatcher.Scheduler));

            sequence.Time = 0.875;

            Assert.That(sequence.Time, Is.EqualTo(0.625));
            Assert.That(hits, Is.EqualTo(new[] { 1, 2 }));

            dispatcher.Update(0.25);
            Assert.That(hits, Is.EqualTo(new[] { 1, 2, 3 }));
        }

        [Test]
        public void Test_CallbackSeeksBackBeforeItself_ItIsInvokedAgainWhenReachedAgain()
        {
            var hits = 0;
            var sequence = default(MotionHandle);
            sequence = LSequence.Create()
                .AppendInterval(1f)
                .InsertCallback(0.5f, () =>
                {
                    hits++;
                    if (hits == 1) sequence.Time = 0.25;
                })
                .Run(b => b.WithScheduler(dispatcher.Scheduler));

            sequence.Time = 0.75;
            Assert.That(hits, Is.EqualTo(1));
            Assert.That(sequence.Time, Is.EqualTo(0.25));

            dispatcher.Update(0.5);
            Assert.That(hits, Is.EqualTo(2));
            sequence.Cancel();
        }
    
        // Two callbacks at the same position; the first goes back to that position (and pauses): the second one has been
        // reached there as well and is still invoked.
        [Test]
        public void Test_CallbackGoesToItsOwnPosition_TheOtherCallbackAtThatPositionIsInvoked()
        {
            var callbacks = new List<int>();
            var sequence = default(MotionHandle);
            sequence = LSequence.Create()
                .AppendInterval(1f)
                .InsertCallback(0.5f, () => { callbacks.Add(1); sequence.Goto(0.5); })
                .InsertCallback(0.5f, () => callbacks.Add(2))
                .Run(b => b.WithScheduler(dispatcher.Scheduler));

            sequence.Time = 0.75;
            dispatcher.Update(0.125);

            Assert.That(sequence.Time, Is.EqualTo(0.5));
            Assert.That(sequence.IsPaused(), Is.True);
            Assert.That(callbacks, Is.EqualTo(new[] { 1, 2 }));
            sequence.Cancel();
        }

        // The exception handler rethrows the exception of a sequence callback: the update ends there, and the sequence
        // is left in a consistent state (going back does not invoke anything).
        [Test]
        public void Test_ExceptionHandlerThrows_TheSequenceUpdateEndsConsistently()
        {
            var callbacks = new List<int>();
            var sequence = LSequence.Create()
                .AppendInterval(1f)
                .InsertCallback(0.25f, () => { callbacks.Add(1); throw new System.ArgumentException("callback"); })
                .InsertCallback(0.5f, () => callbacks.Add(2))
                .Run(b => b.WithScheduler(dispatcher.Scheduler));

            var saved = MotionDispatcher.GetUnhandledExceptionHandler();
            MotionDispatcher.RegisterUnhandledExceptionHandler(ex => throw new System.InvalidOperationException("handler", ex));
            try
            {
                Assert.Throws<System.InvalidOperationException>(() => sequence.Time = 0.75);
            }
            finally
            {
                MotionDispatcher.RegisterUnhandledExceptionHandler(saved);
            }

            sequence.Time = 0.5;
            Assert.That(callbacks, Is.EqualTo(new[] { 1 }));
            sequence.Time = 0.75;
            Assert.That(callbacks, Is.EqualTo(new[] { 1, 2 }));
            sequence.Cancel();
        }

        // A bind of a motion played backwards assigns PlaybackSpeed (which ends the backwards mode, the last fork feature of
        // the motion) and then moves the motion: the evaluation in progress is still superseded.
        [Test]
        public void Test_BindEndsTheBackwardsModeAndSetsTime_TheEvaluationIsSuperseded()
        {
            var completes = 0;
            var armed = false;
            var handle = default(MotionHandle);
            handle = Create().WithOnComplete(() => completes++).Bind(_ =>
            {
                if (!armed) return;
                armed = false;
                handle.PlaybackSpeed = 1f;
                handle.Time = 0.25;
            });
            handle.PlayBackwards();

            armed = true;
            handle.Time = 1.25;

            Assert.That(handle.IsActive(), Is.True);
            Assert.That(handle.Time, Is.EqualTo(0.25));
            Assert.That(completes, Is.Zero);
        }

        // Not supported (as in v2.0.2): clearing the whole dispatcher from a callback. Only memory safety is guaranteed:
        // nothing throws and the motions of the cleared dispatcher are gone.
        [TestCase(false)]
        [TestCase(true)]
        public void Test_ClearInSequenceCallback_DoesNotThrow(bool global)
        {
            var sequence = LSequence.Create()
                .Append(Create(0.25f).RunWithoutBinding())
                .InsertCallback(0.25f, () =>
                {
                    if (global) MotionDispatcher.Clear();
                    else dispatcher.Reset();
                })
                .InsertCallback(0.5f, () => { })
                .Insert(0.5f, Create(0.25f).RunWithoutBinding())
                .Run(b =>
                {
                    if (!global) b.WithScheduler(dispatcher.Scheduler);
                });

            Assert.DoesNotThrow(() => sequence.Time = 0.75);
            Assert.That(sequence.IsValid(), Is.False);
            Assert.DoesNotThrow(() => dispatcher.Update(0.25));
        }
}
}
