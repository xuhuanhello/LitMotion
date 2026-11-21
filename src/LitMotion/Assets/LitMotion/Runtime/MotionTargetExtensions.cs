using System.Runtime.CompilerServices;

namespace LitMotion
{
    /// <summary>
    /// Provides methods for associating motions with a target.
    /// </summary>
    public static class MotionTargetExtensions
    {
        /// <summary>
        /// Associates the motion with a target, so that it can be canceled or completed with <see cref="LMotion.CancelAll(object)"/>
        /// and <see cref="LMotion.CompleteAll(object)"/>. Calling it again replaces the target. Destroying a target object does not
        /// cancel its motions: combine with <c>AddTo()</c> for that.
        /// </summary>
        /// <param name="handle">This motion handle</param>
        /// <param name="target">Target object or identifier, compared with Equals</param>
        /// <returns>Returns itself for method chaining</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static MotionHandle SetTarget(this MotionHandle handle, object target)
        {
            MotionTargetRegistry.Register(handle, target);
            return handle;
        }
    }
}
