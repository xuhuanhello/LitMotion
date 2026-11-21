using System;

namespace LitMotion
{
    // Callbacks of a sequence. The rules are in README.md (1.1), the re-entrance contract in README.md (4).
    //
    // Each loop gives each callback one trigger point, where the playhead (the position that drives the child motions) reaches
    // its position; the mirrored loops of Yoyo/Flip meet the callbacks in descending order. The state is the playhead (loop,
    // phase, position) and the number of callbacks of its loop already invoked. Moving forward invokes the callbacks up to the
    // new location, loop by loop; moving backward only re-arms them. While an update is in progress the playhead stays at the
    // last invoked callback, so that a callback that re-enters the sequence continues from a consistent position.
    //
    // Only guarded sequences run these updates (isGuarded); after user code, an update goes on only if IsCurrent(update).
    internal sealed partial class MotionSequenceSource
    {
        enum CallbackPhase : byte
        {
            Before = 0,   // in the delay before the loop is played
            InPlay = 1,
            AfterEnd = 2, // the motion has completed (last loop)
        }

        struct CallbackLocation
        {
            public long Loop;
            public CallbackPhase Phase;
            public double Position; // playhead position inside the loop (0 to duration), for InPlay
        }

        // An update of the callbacks, or a release of the children, of a guarded sequence. The default value is the update
        // of a sequence that is not guarded: it never checks anything and never invokes a callback.
        readonly struct SequenceUpdate
        {
            public readonly int UpdateVersion;           // updateVersion when the update started (0: not guarded)
            public readonly int SourceVersion;           // version of the source (incremented when it is returned to the pool)
            public readonly MotionEvaluation Evaluation; // evaluation of the sequence motion that runs the update

            public SequenceUpdate(int updateVersion, int sourceVersion, MotionEvaluation evaluation)
            {
                UpdateVersion = updateVersion;
                SourceVersion = sourceVersion;
                Evaluation = evaluation;
            }

            public bool IsGuarded => UpdateVersion != 0;
        }

        // Called by the builder once the sequence motion exists. The sequence is guarded if it has callbacks, a guarded child,
        // or if its motion is guarded (e.g. WithOnStart on the sequence). Otherwise nothing changes: it behaves as in v2.0.2.
        public void SetCallbacks(MotionSequenceCallback[] buffer, int count, bool hasGuardedChildren)
        {
            if (count == 0 && !hasGuardedChildren && !MotionManager.GetManagedDataRef(handle, false).Extension.IsGuarded)
            {
                MotionSequenceCallback.ReturnBuffer(buffer, count);
                return;
            }

            callbackBuffer = buffer;
            callbackCount = count;
            this.hasGuardedChildren = hasGuardedChildren;
            EnableGuard();
        }

        /// <summary>
        /// Makes the updates of this sequence guarded (also called when its motion becomes guarded later, e.g. PlayBackwards()).
        /// </summary>
        public void EnableGuard()
        {
            if (isGuarded) return;

            isGuarded = true;
            NextUpdateVersion();
            ResetCallbackState();
            loopType = MotionManager.GetDataRef(handle, false).Parameters.LoopType;

            ref var managed = ref MotionManager.GetManagedDataRef(handle, false);
            managed.Extension.SetGuardedSequence();

            // The release of the children (OnComplete/OnCancel of this object, registered first by the builder) invokes the
            // callbacks of the user itself, after the release, and not at all if a callback of a child superseded the release.
            userOnComplete = (Action)Delegate.Remove(managed.OnCompleteAction, onCompleteDelegate);
            managed.OnCompleteAction = onCompleteDelegate;
            userOnCancel = (Action)Delegate.Remove(managed.OnCancelAction, onCancelDelegate);
            managed.OnCancelAction = onCancelDelegate;
        }

        // Called by Return(). The motion of a guarded sequence is detached from this object before it goes back to the pool:
        // a later evaluation of that motion (its handle may still be used) binds nothing and releases nothing, and the callbacks
        // of the user stay with that motion.
        void ReleaseCallbacks()
        {
            version++;
            if (!isGuarded) return;

            if (MotionManager.IsValid(handle))
            {
                ref var managed = ref MotionManager.GetManagedDataRef(handle, false);
                if (managed.State0 == this)
                {
                    managed.State0 = null;
                    managed.UpdateAction = null;
                    managed.OnCompleteAction = userOnComplete;
                    managed.OnCancelAction = userOnCancel;
                }
            }

            NextUpdateVersion();
            MotionSequenceCallback.ReturnBuffer(callbackBuffer, callbackCount);
            callbackBuffer = null;
            callbackCount = 0;
            isGuarded = false;
            hasGuardedChildren = false;
            userOnComplete = null;
            userOnCancel = null;
            ResetCallbackState();
        }

        void ResetCallbackState()
        {
            playheadLoop = 0;
            playheadPhase = CallbackPhase.Before;
            playheadLegPosition = 0;
            invokedCount = 0;
            lastUpdateTime = 0;
            openUpdateVersion = 0;
            targetInvokedCount = 0;
            interleaveCallbacks = false;
            targetTime = 0;
            targetLocation = default;
        }

        // The single test of a guarded update: it goes on after user code only if no other update of this sequence has started,
        // the sequence has not been released (version), and the evaluation of its motion is still current (not evaluated again,
        // canceled, removed or reset; see MotionEvaluation). An unguarded update is always current.
        bool IsCurrent(in SequenceUpdate update)
        {
            if (!update.IsGuarded) return true;
            return update.UpdateVersion == updateVersion && update.SourceVersion == version && MotionManager.IsCurrent(update.Evaluation);
        }

        SequenceUpdate BeginUpdate()
        {
            return new SequenceUpdate(NextUpdateVersion(), version, MotionManager.CaptureEvaluation(handle));
        }

        int NextUpdateVersion()
        {
            if (++updateVersion == 0) ++updateVersion;
            return updateVersion;
        }

        SequenceUpdate BeginRelease()
        {
            if (!isGuarded || !MotionManager.IsValid(handle)) return default;
            return new SequenceUpdate(updateVersion, version, MotionManager.CaptureEvaluation(handle));
        }

        bool BeginCallbacks(out SequenceUpdate update)
        {
            update = default;
            if (!isGuarded) return true;

            update = BeginUpdate();
            ref var core = ref MotionManager.GetDataRef(handle, false);
            var time = core.State.Time;
            var completes = core.State.Status == MotionStatus.Completed || MotionExtensionHooks.IsCompletionHeld(update.Evaluation);
            var target = completes ? LocateCompletion(ref core, time) : Locate(ref core, time);
            if (!MoveTo(ref core, time, target, update, out var direction)) return false;

            // The setter evaluates the child motions and invokes the callbacks of the last loop between them (unless the loop is mirrored).
            if (direction > 0) interleaveCallbacks = !IsMirrored(playheadLoop);
            return true;
        }

        bool InvokeCallbacks(double childPosition, in SequenceUpdate update)
        {
            if (!update.IsGuarded) return true;
            if (!IsCurrent(update)) return false;
            return !interleaveCallbacks || InvokeUpTo(targetInvokedCount, childPosition, update);
        }

        void EndCallbacks(in SequenceUpdate update)
        {
            if (!update.IsGuarded || !IsCurrent(update)) return;
            if (!InvokeUpTo(targetInvokedCount, double.PositiveInfinity, update)) return;
            Commit();
        }

        // Called when the setter exits: an update that is still open (an exception escaped from a callback or from the
        // exception handler) ends at its time, without invoking the remaining callbacks; they stay armed.
        void AbortCallbacks(in SequenceUpdate update)
        {
            if (!update.IsGuarded || update.UpdateVersion != updateVersion || openUpdateVersion != update.UpdateVersion) return;
            Commit();
        }

        /// <summary>
        /// Invokes the callbacks up to the current time of the sequence motion when its value is not bound in this update
        /// (e.g. in a delay with skipValuesDuringDelay).
        /// </summary>
        public void DispatchCallbacksWithoutBinding()
        {
            if (!isGuarded) return;
            ref var core = ref MotionManager.GetDataRef(handle, false);
            var time = core.State.Time;
            UpdateImmediately(ref core, time, core.State.Status == MotionStatus.Completed ? LocateCompletion(ref core, time) : Locate(ref core, time));
        }

        // Called by the parent sequence when its playhead passes a loop boundary: the child motions are not evaluated
        // there, but the callbacks of this (nested) sequence must follow the playhead.
        void CatchUp(double time)
        {
            if (!isGuarded) return;
            ref var core = ref MotionManager.GetDataRef(handle, false);
            UpdateImmediately(ref core, time, Locate(ref core, time));
        }

        // An update that does not evaluate the child motions: the nested sequences follow the playhead here, and every callback
        // reached is invoked.
        void UpdateImmediately(ref MotionData core, double time, in CallbackLocation target)
        {
            var update = BeginUpdate();
            if (!MoveTo(ref core, time, target, update, out var direction)) return;
            if (direction > 0 && !AdvanceLoop(ref core, targetInvokedCount, PlayheadAt(ref core, target), update)) return;
            if (direction < 0 && !MoveChildren(PlayheadAt(ref core, target), update)) return;
            if (!InvokeUpTo(targetInvokedCount, double.PositiveInfinity, update)) return;
            Commit();
        }

        // Moves the callback playhead to `target`, the location of `time` (raw time of the sequence motion). Moving forward
        // finishes the loops that the playhead leaves; moving backward re-arms the trigger points after the new time.
        bool MoveTo(ref MotionData core, double time, in CallbackLocation target, in SequenceUpdate update, out int direction)
        {
            direction = GetDirection(time, target);
            openUpdateVersion = update.UpdateVersion;
            targetTime = time;
            targetLocation = target;
            interleaveCallbacks = false;

            if (direction > 0) return MoveForward(ref core, target, update);
            if (direction < 0) Rearm(time, target);
            else targetInvokedCount = invokedCount;
            return true;
        }

        int GetDirection(double time, in CallbackLocation target)
        {
            // Inside a callback of this sequence: compare with the position of the last invoked callback. The other callbacks at
            // that position have been reached as well (the update was going past them).
            if (openUpdateVersion != 0) return CompareWithPlayhead(target) < 0 ? -1 : 1;

            if (time != lastUpdateTime) return time > lastUpdateTime ? 1 : -1;

            // Same time: only a completion (e.g. Complete() of a zero-length sequence) moves forward.
            return target.Phase == CallbackPhase.AfterEnd && CompareWithPlayhead(target) > 0 ? 1 : 0;
        }

        bool MoveForward(ref MotionData core, in CallbackLocation target, in SequenceUpdate update)
        {
            while (playheadLoop < target.Loop)
            {
                if (!AdvanceLoop(ref core, callbackCount, LoopEndPlayhead(ref core, playheadLoop), update)) return false;
                if (!StartNextLoop(ref core, update)) return false;
            }

            targetInvokedCount = Math.Max(ReachedCount(target), invokedCount);
            return true;
        }

        // In the current loop, a callback stays invoked if its position is before the new position, or if it was invoked at or
        // before the new time (with a non-monotonic ease, the position may fall back below a callback reached earlier in the loop).
        void Rearm(double time, in CallbackLocation target)
        {
            var before = StrictlyBeforeCount(target);
            invokedCount = target.Loop == playheadLoop ? Math.Min(invokedCount, Math.Max(before, InvokedAtOrBeforeCount(time))) : before;
            playheadLoop = target.Loop;
            targetInvokedCount = invokedCount;
            SetPlayhead(target);
        }

        void Commit()
        {
            lastUpdateTime = targetTime;
            SetPlayhead(targetLocation);
            openUpdateVersion = 0;
        }

        void SetPlayhead(in CallbackLocation location)
        {
            playheadPhase = location.Phase;
            playheadLegPosition = location.Phase == CallbackPhase.InPlay
                ? (IsMirrored(location.Loop) ? -location.Position : location.Position)
                : 0;
        }

        // Invokes the callbacks of the current loop up to `targetCount`, keeping nested sequences in step:
        // callbacks at or before the position of a nested sequence are invoked before it.
        bool AdvanceLoop(ref MotionData core, int targetCount, double playhead, in SequenceUpdate update)
        {
            if (hasGuardedChildren)
            {
                var mirrored = IsMirrored(playheadLoop);
                var items = Items;
                for (int i = 0; i < items.Length; i++)
                {
                    var item = items[i];
                    if (!TryGetCallbackChild(item.Handle, out var child)) continue;
                    if (!mirrored && !InvokeUpTo(targetCount, item.Position, update)) return false;

                    child.CatchUp(playhead - item.Position);
                    if (!IsCurrent(update)) return false;
                }
            }

            return InvokeUpTo(targetCount, double.PositiveInfinity, update);
        }

        bool StartNextLoop(ref MotionData core, in SequenceUpdate update)
        {
            playheadLoop++;
            invokedCount = 0;
            playheadPhase = CallbackPhase.Before;
            playheadLegPosition = 0;

            // With Restart the playhead jumps back to the start: nested sequences are re-armed.
            if (loopType == LoopType.Restart)
            {
                return MoveChildren(LoopStartPlayhead(ref core, playheadLoop), update);
            }

            return true;
        }

        bool MoveChildren(double playhead, in SequenceUpdate update)
        {
            if (!hasGuardedChildren) return true;

            var items = Items;
            for (int i = 0; i < items.Length; i++)
            {
                var item = items[i];
                if (!TryGetCallbackChild(item.Handle, out var child)) continue;
                child.CatchUp(playhead - item.Position);
                if (!IsCurrent(update)) return false;
            }

            return true;
        }

        bool InvokeUpTo(int targetCount, double maxPosition, in SequenceUpdate update)
        {
            var mirrored = IsMirrored(playheadLoop);

            while (invokedCount < targetCount)
            {
                var item = callbackBuffer[mirrored ? callbackCount - 1 - invokedCount : invokedCount];
                if (!mirrored && item.Position > maxPosition) break;

                // Advance first, so that a re-entrant update does not invoke the same callback again.
                callbackBuffer[mirrored ? callbackCount - 1 - invokedCount : invokedCount].InvokedAt = targetTime;
                invokedCount++;
                playheadPhase = CallbackPhase.InPlay;
                playheadLegPosition = mirrored ? -item.Position : item.Position;

                try
                {
                    item.Callback?.Invoke();
                }
                catch (Exception ex)
                {
                    MotionDispatcher.GetUnhandledExceptionHandler()?.Invoke(ex);
                }

                if (!IsCurrent(update)) return false;
            }

            return true;
        }

        static bool TryGetCallbackChild(MotionHandle handle, out MotionSequenceSource child)
        {
            child = null;
            if (!MotionManager.IsActive(handle)) return false;

            ref var managed = ref MotionManager.GetManagedDataRef(handle, false);
            if (!managed.Extension.IsGuardedSequence) return false;

            child = managed.State0 as MotionSequenceSource;
            return child != null;
        }

        int CompareWithPlayhead(in CallbackLocation target)
        {
            if (target.Loop != playheadLoop) return target.Loop > playheadLoop ? 1 : -1;
            if (target.Phase != playheadPhase) return target.Phase > playheadPhase ? 1 : -1;
            if (target.Phase != CallbackPhase.InPlay) return 0;

            var legPosition = IsMirrored(target.Loop) ? -target.Position : target.Position;
            return legPosition > playheadLegPosition ? 1 : legPosition < playheadLegPosition ? -1 : 0;
        }

        // Number of callbacks of the loop of `target` whose trigger point has been reached at `target`.
        int ReachedCount(in CallbackLocation target)
        {
            switch (target.Phase)
            {
                case CallbackPhase.Before: return 0;
                case CallbackPhase.AfterEnd: return callbackCount;
            }

            var count = 0;
            if (IsMirrored(target.Loop))
            {
                while (count < callbackCount && callbackBuffer[callbackCount - 1 - count].Position >= target.Position) count++;
            }
            else
            {
                while (count < callbackCount && callbackBuffer[count].Position <= target.Position) count++;
            }
            return count;
        }

        // Number of the callbacks invoked in the current loop (in the order of the loop) that were invoked at or before `time`.
        int InvokedAtOrBeforeCount(double time)
        {
            var mirrored = IsMirrored(playheadLoop);
            var count = 0;
            while (count < invokedCount && callbackBuffer[mirrored ? callbackCount - 1 - count : count].InvokedAt <= time) count++;
            return count;
        }

        // Number of callbacks of the loop of `target` whose trigger point is strictly before `target`.
        int StrictlyBeforeCount(in CallbackLocation target)
        {
            switch (target.Phase)
            {
                case CallbackPhase.Before: return 0;
                case CallbackPhase.AfterEnd: return callbackCount;
            }

            var count = 0;
            if (IsMirrored(target.Loop))
            {
                while (count < callbackCount && callbackBuffer[callbackCount - 1 - count].Position > target.Position) count++;
            }
            else
            {
                while (count < callbackCount && callbackBuffer[count].Position < target.Position) count++;
            }
            return count;
        }

        // Location of `time` (raw time of the sequence motion): the loop arithmetic of MotionData.Update, with a 64-bit loop index.
        CallbackLocation Locate(ref MotionData core, double time)
        {
            if (time < 0) return default;
            if (core.Parameters.Duration <= 0f) return LocateZeroDuration(ref core.Parameters, time);
            return core.Parameters.DelayType == DelayType.FirstLoop ? LocateWithFirstDelay(ref core, time) : LocateWithEveryDelay(ref core, time);
        }

        // Location of an update that completes the sequence motion.
        CallbackLocation LocateCompletion(ref MotionData core, double time)
        {
            return core.Parameters.Loops >= 0 ? EndOf(core.Parameters.Loops) : Locate(ref core, time);
        }

        static CallbackLocation EndOf(int loops)
        {
            return new CallbackLocation { Loop = Math.Max(0, loops - 1), Phase = CallbackPhase.AfterEnd };
        }

        // A zero-length loop is played at the end of each delay.
        static CallbackLocation LocateZeroDuration(ref MotionData.MotionParameters parameters, double time)
        {
            var delay = (double)parameters.Delay;
            if (parameters.DelayType == DelayType.FirstLoop || delay == 0)
            {
                if (time < delay) return default;
                if (time > delay && parameters.Loops >= 0) return EndOf(parameters.Loops);
                return new CallbackLocation { Loop = 0, Phase = CallbackPhase.InPlay, Position = 0 };
            }

            var loop = (long)Math.Floor(time / delay);
            if (parameters.Loops >= 0 && loop >= parameters.Loops) return EndOf(parameters.Loops);
            return new CallbackLocation { Loop = loop, Phase = CallbackPhase.Before };
        }

        CallbackLocation LocateWithFirstDelay(ref MotionData core, double time)
        {
            ref var parameters = ref core.Parameters;
            var delay = (double)parameters.Delay;
            if (time < delay) return default;

            var elapsedLoops = (time - delay) / parameters.Duration;
            var loop = (long)Math.Floor(elapsedLoops);
            if (parameters.Loops >= 0 && loop >= parameters.Loops) return EndOf(parameters.Loops);
            return InLoop(ref core, loop, elapsedLoops - loop);
        }

        CallbackLocation LocateWithEveryDelay(ref MotionData core, double time)
        {
            ref var parameters = ref core.Parameters;
            var delay = (double)parameters.Delay;
            var period = delay + parameters.Duration;
            var loop = (long)Math.Floor(time / period);
            if (parameters.Loops >= 0 && loop >= parameters.Loops) return EndOf(parameters.Loops);

            var timeInLoop = time - loop * period - delay;
            if (timeInLoop < 0) return new CallbackLocation { Loop = loop, Phase = CallbackPhase.Before };
            return InLoop(ref core, loop, timeInLoop / parameters.Duration);
        }

        CallbackLocation InLoop(ref MotionData core, long loop, double loopProgress)
        {
            return new CallbackLocation { Loop = loop, Phase = CallbackPhase.InPlay, Position = PositionAt(ref core, loop, Math.Min(loopProgress, 1.0)) };
        }

        bool IsMirrored(long loop)
        {
            return (loopType == LoopType.Yoyo || loopType == LoopType.Flip) && (loop & 1) == 1;
        }

        // Playhead position inside a loop at normalized time t.
        double PositionAt(ref MotionData core, long loop, double t)
        {
            if (duration <= 0) return 0;

            if (IsMirrored(loop))
            {
                return loopType == LoopType.Yoyo
                    ? EvaluateEase(ref core, 1 - t) * duration
                    : (1 - EvaluateEase(ref core, t)) * duration;
            }

            return EvaluateEase(ref core, t) * duration;
        }

        // Value of the sequence motion (the time given to the child motions) at a location.
        double PlayheadAt(ref MotionData core, in CallbackLocation location)
        {
            switch (location.Phase)
            {
                case CallbackPhase.Before: return LoopStartPlayhead(ref core, location.Loop);
                case CallbackPhase.AfterEnd: return LoopEndPlayhead(ref core, location.Loop);
                default: return LoopOffset(ref core, location.Loop) + location.Position;
            }
        }

        double LoopStartPlayhead(ref MotionData core, long loop) => LoopOffset(ref core, loop) + PositionAt(ref core, loop, 0);
        double LoopEndPlayhead(ref MotionData core, long loop) => LoopOffset(ref core, loop) + PositionAt(ref core, loop, 1);

        // With Incremental loops the value keeps increasing from one loop to the next.
        double LoopOffset(ref MotionData core, long loop)
        {
            if (loopType != LoopType.Incremental || duration <= 0) return 0;
            return loop * EvaluateEase(ref core, 1) * duration;
        }

        static double EvaluateEase(ref MotionData core, double t)
        {
            return core.Parameters.Ease == Ease.CustomAnimationCurve
                ? core.Parameters.AnimationCurve.Evaluate((float)t)
                : EaseUtility.Evaluate((float)t, core.Parameters.Ease);
        }
    }
}
