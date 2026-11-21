using System;
using System.Runtime.CompilerServices;

namespace LitMotion
{
    /// <summary>
    /// Provides methods for controlling the playback of the motion pointed to by MotionHandle.
    /// </summary>
    public static class MotionHandlePlaybackExtensions
    {
        const int Success = 0;
        const int NotExists = 1;
        const int CanceledOrCompleted = 2;
        const int InSequence = 3;

        /// <summary>
        /// Pauses the motion. Its PlaybackSpeed is remembered and set to 0; assigning PlaybackSpeed directly ends the pause.
        /// </summary>
        /// <param name="handle">This motion handle</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Pause(this MotionHandle handle)
        {
            ThrowIfFailed(PauseCore(handle));
        }
        /// <summary>
        /// Attempts to pause the motion.
        /// </summary>
        /// <param name="handle">This motion handle</param>
        /// <returns>Returns true if the operation was successful.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryPause(this MotionHandle handle)
        {
            return PauseCore(handle) == Success;
        }
        /// <summary>
        /// Resumes a motion paused with Pause() (or stopped at its start by PlayBackwards()), restoring its PlaybackSpeed.
        /// </summary>
        /// <param name="handle">This motion handle</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Play(this MotionHandle handle)
        {
            ThrowIfFailed(PlayCore(handle));
        }
        /// <summary>
        /// Attempts to resume the motion.
        /// </summary>
        /// <param name="handle">This motion handle</param>
        /// <returns>Returns true if the operation was successful.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryPlay(this MotionHandle handle)
        {
            return PlayCore(handle) == Success;
        }
        /// <summary>
        /// Resumes the motion if it is paused, otherwise pauses it.
        /// </summary>
        /// <param name="handle">This motion handle</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void TogglePause(this MotionHandle handle)
        {
            ThrowIfFailed(TogglePauseCore(handle));
        }
        /// <summary>
        /// Attempts to toggle the pause state of the motion.
        /// </summary>
        /// <param name="handle">This motion handle</param>
        /// <returns>Returns true if the operation was successful.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryTogglePause(this MotionHandle handle)
        {
            return TogglePauseCore(handle) == Success;
        }
        /// <summary>
        /// Checks if the motion is paused by Pause() or stopped at its start by PlayBackwards().
        /// </summary>
        /// <param name="handle">This motion handle</param>
        /// <returns>True if the motion is paused, otherwise false.</returns>
        public static bool IsPaused(this MotionHandle handle)
        {
            if (GetStatus(handle) != Success) return false;
            return MotionManager.GetDataRef(handle, false).State.IsPaused;
        }
        /// <summary>
        /// Plays the motion forward: resumes it and makes PlaybackSpeed positive.
        /// </summary>
        /// <param name="handle">This motion handle</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void PlayForward(this MotionHandle handle)
        {
            ThrowIfFailed(PlayForwardCore(handle));
        }
        /// <summary>
        /// Attempts to play the motion forward.
        /// </summary>
        /// <param name="handle">This motion handle</param>
        /// <returns>Returns true if the operation was successful.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryPlayForward(this MotionHandle handle)
        {
            return PlayForwardCore(handle) == Success;
        }
        /// <summary>
        /// Plays the motion backwards: resumes it, makes PlaybackSpeed negative, and pauses it when it reaches its start.
        /// </summary>
        /// <param name="handle">This motion handle</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void PlayBackwards(this MotionHandle handle)
        {
            ThrowIfFailed(PlayBackwardsCore(handle));
        }
        /// <summary>
        /// Attempts to play the motion backwards.
        /// </summary>
        /// <param name="handle">This motion handle</param>
        /// <returns>Returns true if the operation was successful.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryPlayBackwards(this MotionHandle handle)
        {
            return PlayBackwardsCore(handle) == Success;
        }
        /// <summary>
        /// Restarts the motion from Time 0 and plays it forward. OnStart is not invoked again.
        /// </summary>
        /// <param name="handle">This motion handle</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Restart(this MotionHandle handle)
        {
            ThrowIfFailed(RestartCore(handle));
        }
        /// <summary>
        /// Attempts to restart the motion.
        /// </summary>
        /// <param name="handle">This motion handle</param>
        /// <returns>Returns true if the operation was successful.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryRestart(this MotionHandle handle)
        {
            return RestartCore(handle) == Success;
        }

        /// <summary>
        /// Moves the motion to the specified time (including its delay) and pauses it, or resumes it if <paramref name="andPlay"/> is true.
        /// Reaching the end completes the motion.
        /// </summary>
        /// <param name="handle">This motion handle</param>
        /// <param name="time">Time to go to</param>
        /// <param name="andPlay">Whether to resume playback after moving</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Goto(this MotionHandle handle, double time, bool andPlay = false)
        {
            ThrowIfFailed(GotoCore(handle, time, andPlay));
        }

        /// <summary>
        /// Attempts to move the motion to the specified time.
        /// </summary>
        /// <param name="handle">This motion handle</param>
        /// <param name="time">Time to go to</param>
        /// <param name="andPlay">Whether to resume playback after moving</param>
        /// <returns>Returns true if the operation was successful.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="time"/> is NaN, or positive infinity on a motion that loops infinitely.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryGoto(this MotionHandle handle, double time, bool andPlay = false)
        {
            return GotoCore(handle, time, andPlay) == Success;
        }

        static int PauseCore(MotionHandle handle)
        {
            var status = GetStatus(handle);
            if (status != Success) return status;

            ref var state = ref MotionManager.GetDataRef(handle, false).State;
            ref var extension = ref MotionManager.GetManagedDataRef(handle, false).Extension;
            if (!state.IsPaused)
            {
                extension.Pause(ref state, state.PlaybackSpeed);
            }
            return Success;
        }

        static int PlayCore(MotionHandle handle)
        {
            var status = GetStatus(handle);
            if (status != Success) return status;

            Resume(handle);
            return Success;
        }

        static int TogglePauseCore(MotionHandle handle)
        {
            var status = GetStatus(handle);
            if (status != Success) return status;

            return IsPaused(handle) ? PlayCore(handle) : PauseCore(handle);
        }

        static int PlayForwardCore(MotionHandle handle)
        {
            var status = GetStatus(handle);
            if (status != Success) return status;

            var speed = Resume(handle);
            ref var state = ref MotionManager.GetDataRef(handle, false).State;
            state.ClearPlayingBackwards();
            state.PlaybackSpeed = Math.Abs(speed);

            if (state.Time < 0) MotionManager.SetTime(handle, 0);
            return Success;
        }

        static int PlayBackwardsCore(MotionHandle handle)
        {
            var status = GetStatus(handle);
            if (status != Success) return status;

            var speed = -Math.Abs(Resume(handle));
            ref var data = ref MotionManager.GetDataRef(handle, false);
            ref var managedData = ref MotionManager.GetManagedDataRef(handle, false);
            ref var extension = ref managedData.Extension;
            extension.SetGuarded();
            data.State.SetPlayingBackwards();
            if (managedData.State0 is MotionSequenceSource sequence) sequence.EnableGuard();

            var startTime = MotionExtensionHooks.GetStartTime(ref data);
            var totalDuration = data.Parameters.TotalDuration;
            var time = data.State.Time;

            if (time <= startTime)
            {
                // Already at (or before) the start: stay paused there. Pause first so that OnStart stays deferred.
                extension.Pause(ref data.State, speed);
                MotionManager.StopAtStart(handle);
                return Success;
            }

            data.State.PlaybackSpeed = speed;

            // A preserved motion keeps advancing its time after completion; go back from the end right away.
            if (time > totalDuration) MotionManager.SetTime(handle, totalDuration);
            return Success;
        }

        static int RestartCore(MotionHandle handle)
        {
            var status = GetStatus(handle);
            if (status != Success) return status;

            var speed = Resume(handle);
            ref var state = ref MotionManager.GetDataRef(handle, false).State;
            state.ClearPlayingBackwards();
            state.PlaybackSpeed = Math.Abs(speed);
            MotionManager.SetTime(handle, 0);
            return Success;
        }

        static int GotoCore(MotionHandle handle, double time, bool andPlay)
        {
            if (double.IsNaN(time)) throw new ArgumentOutOfRangeException(nameof(time), "The time must not be NaN.");

            var status = GetStatus(handle);
            if (status != Success) return status;

            var totalDuration = MotionManager.GetDataRef(handle, false).Parameters.TotalDuration;
            if (double.IsPositiveInfinity(time) && double.IsInfinity(totalDuration))
            {
                throw new ArgumentOutOfRangeException(nameof(time), "Cannot go to the end of a motion that loops infinitely.");
            }

            // Change the pause state first, so that OnStart stays deferred while paused.
            if (andPlay) Resume(handle);
            else PauseCore(handle);

            time = Math.Min(Math.Max(time, 0.0), totalDuration);
            ref var data = ref MotionManager.GetDataRef(handle, false);
            if (time >= totalDuration && data.Parameters.Duration <= 0f && data.State.Status != MotionStatus.Completed)
            {
                // A zero-duration motion completes once its time is past the end of the delay, not at the end itself.
                MotionManager.TryComplete(handle);
                return Success;
            }

            MotionManager.SetTime(handle, time);
            return Success;
        }

        // Ends the pause (if any) and returns the effective playback speed.
        static float Resume(MotionHandle handle)
        {
            ref var state = ref MotionManager.GetDataRef(handle, false).State;
            ref var extension = ref MotionManager.GetManagedDataRef(handle, false).Extension;

            if (state.IsPaused)
            {
                state.PlaybackSpeed = extension.PausedPlaybackSpeed;
            }
            state.ClearPaused();

            return state.PlaybackSpeed;
        }

        static int GetStatus(MotionHandle handle)
        {
            if (!MotionManager.IsValidHandle(handle)) return NotExists;
            if (!MotionManager.IsActive(handle)) return CanceledOrCompleted;
            if (MotionManager.GetDataRef(handle, false).State.IsInSequence) return InSequence;
            return Success;
        }

        static void ThrowIfFailed(int status)
        {
            switch (status)
            {
                case NotExists:
                    Error.MotionNotExists();
                    break;
                case CanceledOrCompleted:
                    Error.MotionHasBeenCanceledOrCompleted();
                    break;
                case InSequence:
                    Error.MotionIsInSequence();
                    break;
            }
        }
    }
}
