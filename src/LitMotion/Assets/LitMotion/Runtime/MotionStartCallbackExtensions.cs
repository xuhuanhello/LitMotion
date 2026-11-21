using System;
using System.Runtime.CompilerServices;

namespace LitMotion
{
    /// <summary>
    /// Provides the OnStart callback for MotionBuilder.
    /// </summary>
    public static class MotionStartCallbackExtensions
    {
        /// <summary>
        /// Specify the callback when the motion starts playing, at the end of its delay. It is invoked once, before the value of that
        /// update is bound; several calls register several callbacks.
        /// </summary>
        /// <param name="builder">This builder</param>
        /// <param name="callback">Callback when the motion starts</param>
        /// <returns>This builder to allow chaining multiple method calls.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static MotionBuilder<TValue, TOptions, TAdapter> WithOnStart<TValue, TOptions, TAdapter>(this MotionBuilder<TValue, TOptions, TAdapter> builder, Action callback)
            where TValue : unmanaged
            where TOptions : unmanaged, IMotionOptions
            where TAdapter : unmanaged, IMotionAdapter<TValue, TOptions>
        {
            builder.CheckBuffer();

            builder.buffer.OnStartAction = MotionStartCallbacks.Add(builder.buffer.OnStartAction, callback);
            return builder;
        }
    }
}
