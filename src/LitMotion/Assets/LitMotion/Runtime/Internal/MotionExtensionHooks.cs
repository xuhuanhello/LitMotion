using System;
using System.Runtime.CompilerServices;

namespace LitMotion
{
    /// <summary>
    /// Extension points of the shared dispatch (<c>MotionStorage.Dispatch</c>). Each method implements one decision of a
    /// fork feature; the dispatch decides when it applies. Nothing here binds values or invokes the original callbacks.
    /// </summary>
    internal static class MotionExtensionHooks
    {
        /// <summary>
        /// OnStart is due once the motion has passed the end of its first delay (or has completed), unless it is paused by Pause().
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsStartDue(ref MotionData core, ref ManagedMotionData managedData)
        {
            ref var extension = ref managedData.Extension;
            if (extension.OnStart == null) return false;
            if (core.State.Status == MotionStatus.Completed) return true;
            return core.State.Time >= core.Parameters.Delay && !core.State.IsPaused;
        }

        /// <summary>
        /// Removes the pending OnStart callbacks from the motion, so that they run at most once.
        /// Invoke them with <see cref="MotionStartCallbacks.GetCount"/>/<see cref="MotionStartCallbacks.GetAt"/>.
        /// </summary>
        public static Action TakeOnStart(ref ManagedMotionData managedData)
        {
            var callbacks = managedData.Extension.OnStart;
            managedData.Extension.OnStart = null;
            return callbacks;
        }

        /// <summary>
        /// Invokes a callback of a fork feature; exceptions go to the unhandled exception handler.
        /// </summary>
        public static void InvokeCallback(Action callback)
        {
            try
            {
                callback?.Invoke();
            }
            catch (Exception ex)
            {
                MotionDispatcher.GetUnhandledExceptionHandler()?.Invoke(ex);
            }
        }

        /// <summary>
        /// PlayBackwards(): when the runner moves the motion to (or past) its start, evaluate it at the start and pause it there.
        /// </summary>
        /// <returns>True if the motion was stopped at its start: the value of the start must be bound.</returns>
        public static bool StopAtStartWhenPlayingBackwards<TValue, TOptions, TAdapter>(ref MotionData<TValue, TOptions> data, ref ManagedMotionData managedData, ref TValue result)
            where TValue : unmanaged
            where TOptions : unmanaged, IMotionOptions
            where TAdapter : unmanaged, IMotionAdapter<TValue, TOptions>
        {
            ref var state = ref data.Core.State;
            if (!state.IsPlayingBackwards || state.PlaybackSpeed >= 0f || state.Status == MotionStatus.Completed) return false;

            var startTime = GetStartTime(ref data.Core);
            if (state.Time > startTime) return false;

            // Keep the transition of this update (PrevStatus/PrevCompletedLoops) for the lifecycle callbacks.
            var prevStatus = state.PrevStatus;
            var prevCompletedLoops = state.PrevCompletedLoops;
            data.Update<TAdapter>(startTime, out result);
            state.PrevStatus = prevStatus;
            state.PrevCompletedLoops = prevCompletedLoops;

            managedData.Extension.Pause(ref state, state.PlaybackSpeed);
            return true;
        }

        /// <summary>
        /// Time at which a motion played backwards stops: the start of its first playback.
        /// A zero-duration motion with a delay before every loop completes its first loop at Time == Delay, so it stops at 0.
        /// </summary>
        public static double GetStartTime(ref MotionData core)
        {
            ref var parameters = ref core.Parameters;
            if (parameters.Duration <= 0f && parameters.DelayType == DelayType.EveryLoop && parameters.Delay > 0f) return 0.0;
            return parameters.Delay;
        }

        /// <summary>
        /// A guarded sequence whose value is not bound in this evaluation still dispatches its callbacks.
        /// </summary>
        /// <returns>True if user code may have run.</returns>
        public static bool DispatchSequenceCallbacksWithoutBinding(ref ManagedMotionData managedData)
        {
            if (!managedData.Extension.IsGuardedSequence || managedData.State0 is not MotionSequenceSource source) return false;
            source.DispatchCallbacksWithoutBinding();
            return true;
        }

        static MotionEvaluation heldCompletion;

        /// <summary>
        /// The dispatch publishes the completion of a guarded sequence after its update (see <c>MotionStorage.Dispatch</c>):
        /// during the bind the motion is Playing, and the sequence learns that this update completes it from here.
        /// </summary>
        public static void HoldCompletion(in MotionEvaluation evaluation)
        {
            heldCompletion = evaluation;
        }

        public static void ReleaseCompletionHold()
        {
            heldCompletion = default;
        }

        /// <summary>
        /// True if the given evaluation (of a sequence motion) completes the motion, whose completion is held during its update.
        /// </summary>
        public static bool IsCompletionHeld(in MotionEvaluation evaluation)
        {
            return heldCompletion.IsGuarded && evaluation.IsGuarded &&
                heldCompletion.StorageId == evaluation.StorageId &&
                heldCompletion.SlotIndex == evaluation.SlotIndex &&
                heldCompletion.SlotVersion == evaluation.SlotVersion &&
                heldCompletion.StorageGeneration == evaluation.StorageGeneration &&
                heldCompletion.Version == evaluation.Version;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void OnRemoved(ref ManagedMotionData managedData)
        {
            if (managedData.Extension.HasTarget) MotionTargetRegistry.Release(ref managedData.Extension);
        }
    }
}
