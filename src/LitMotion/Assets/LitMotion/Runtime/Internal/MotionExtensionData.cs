using System;
using System.Runtime.CompilerServices;

namespace LitMotion
{
    /// <summary>
    /// Per-motion state of the fork features (OnStart, sequence callbacks, SetTarget, Pause/PlayBackwards).
    /// It lives in <see cref="ManagedMotionData"/>, so it is reset together with the motion entry
    /// (on creation, on removal and on storage reset) and never outlives the motion. Size: 24 bytes.
    /// </summary>
    internal struct MotionExtensionData
    {
        // registrationAndFlags [0..23]: id of the current SetTarget registration (0 = none), see MotionTargetRegistry.
        // registrationAndFlags [24..31]: flags.
        const uint RegistrationMask = 0x00FFFFFFu;
        const uint GuardedFlag = 1u << 24;           // evaluations are guarded (sticky), see IsGuarded
        const uint GuardedSequenceFlag = 1u << 26; // a sequence whose MotionSequenceSource runs guarded updates, with or without callbacks (sticky)

        static uint lastRegistration;

        public MotionExtensionData(Action onStart)
        {
            OnStart = onStart;
            Target = null;
            PausedPlaybackSpeed = 0f;
            registrationAndFlags = onStart == null ? 0u : GuardedFlag;
        }

        /// <summary>
        /// Pending OnStart callbacks (see <see cref="MotionStartCallbacks"/>). Cleared right before they are invoked, so they run at most once.
        /// </summary>
        public Action OnStart;

        public object Target;

        public float PausedPlaybackSpeed; // saved by Pause()

        uint registrationAndFlags;

        /// <summary>
        /// Id of the current SetTarget registration, 0 if none. Every SetTarget() call gets a new id, so a registration
        /// made again after the motion moved to another target is not the same as the previous one.
        /// </summary>
        public readonly uint Registration => registrationAndFlags & RegistrationMask;

        /// <summary>
        /// True if the evaluations of this motion are guarded against re-entrance (see MotionEvaluation): the motion uses
        /// OnStart, has been played backwards, or is a sequence with callbacks or with a guarded child (or itself guarded).
        /// It is sticky: once guarded, a motion stays guarded, so that clearing a playback mode never disables the checks of an
        /// evaluation in progress.
        /// </summary>
        public readonly bool IsGuarded
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => (registrationAndFlags & GuardedFlag) != 0;
        }

        public readonly bool IsGuardedSequence => (registrationAndFlags & GuardedSequenceFlag) != 0;
        public readonly bool HasTarget => Target != null;

        public void Pause(ref MotionData.MotionState state, float speed)
        {
            PausedPlaybackSpeed = speed;
            state.SetPaused();
            state.PlaybackSpeed = 0f;
        }

        public void SetGuarded() => registrationAndFlags |= GuardedFlag;

        public void SetGuardedSequence()
        {
            registrationAndFlags |= GuardedSequenceFlag | GuardedFlag;
        }

        /// <returns>The id of the new registration.</returns>
        public uint SetTarget(object target)
        {
            if (++lastRegistration > RegistrationMask) lastRegistration = 1;
            Target = target;
            registrationAndFlags = (registrationAndFlags & ~RegistrationMask) | lastRegistration;
            return lastRegistration;
        }

        public void ClearTarget()
        {
            Target = null;
            registrationAndFlags &= ~RegistrationMask;
        }
    }
}
