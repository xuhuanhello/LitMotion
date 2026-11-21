using System;
using System.Runtime.CompilerServices;

namespace LitMotion
{
    /// <summary>
    /// Provides methods for adding callbacks to a sequence.
    /// </summary>
    /// <remarks>
    /// A callback is invoked once per loop when the time of the sequence moves forward past its position (at the mirrored position
    /// in the return of Yoyo/Flip), also when Time is set forward or the sequence is completed; moving backward invokes nothing.
    /// </remarks>
    public static class SequenceCallbackExtensions
    {
        /// <summary>
        /// Adds a callback at the end of the sequence, where the next Append() would start. Like Append(), it ignores the motions
        /// and callbacks added with Insert().
        /// </summary>
        /// <param name="builder">This sequence builder</param>
        /// <param name="callback">Callback to invoke when the playhead reaches the position</param>
        /// <returns>This builder to allow chaining multiple method calls.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static MotionSequenceBuilder AppendCallback(this MotionSequenceBuilder builder, Action callback)
        {
            builder.CheckIsDisposed();
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            builder.source.AppendCallback(callback);
            return builder;
        }

        /// <summary>
        /// Inserts a callback at the specified position. The sequence is extended if needed; where the next Append() or Join() is
        /// placed does not change.
        /// </summary>
        /// <param name="builder">This sequence builder</param>
        /// <param name="position">Position (seconds from the start of the sequence)</param>
        /// <param name="callback">Callback to invoke when the playhead reaches the position</param>
        /// <returns>This builder to allow chaining multiple method calls.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static MotionSequenceBuilder InsertCallback(this MotionSequenceBuilder builder, float position, Action callback)
        {
            builder.CheckIsDisposed();
            if (!(position >= 0f) || float.IsInfinity(position)) throw new ArgumentOutOfRangeException(nameof(position), "The position must be a finite value greater than or equal to 0.");
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            builder.source.InsertCallback(position, callback);
            return builder;
        }
    }
}
