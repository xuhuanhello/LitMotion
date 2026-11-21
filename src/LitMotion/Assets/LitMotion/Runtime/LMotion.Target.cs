using System.Runtime.CompilerServices;

namespace LitMotion
{
    public static partial class LMotion
    {
        /// <summary>
        /// Cancels all motions associated with the target by SetTarget(). Motions in a sequence are skipped.
        /// </summary>
        /// <param name="target">Target object or identifier</param>
        /// <returns>The number of motions canceled.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int CancelAll(object target)
        {
            return MotionTargetRegistry.Cancel(target);
        }

        /// <summary>
        /// Completes all motions associated with the target by SetTarget(). Motions that loop infinitely are canceled instead,
        /// and motions in a sequence are skipped.
        /// </summary>
        /// <param name="target">Target object or identifier</param>
        /// <returns>The number of motions completed (or canceled).</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int CompleteAll(object target)
        {
            return MotionTargetRegistry.Complete(target);
        }
    }
}
