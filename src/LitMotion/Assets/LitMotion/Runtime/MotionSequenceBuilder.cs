using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using LitMotion.Adapters;
using LitMotion.Collections;

namespace LitMotion
{
    internal sealed class MotionSequenceBuilderSource : ILinkedPoolNode<MotionSequenceBuilderSource>
    {
        static LinkedPool<MotionSequenceBuilderSource> pool;

        public static MotionSequenceBuilderSource Rent()
        {
            if (!pool.TryPop(out var result)) result = new();
            return result;
        }

        public static void Return(MotionSequenceBuilderSource source)
        {
            if (source.buffer != null)
            {
                ArrayPool<MotionSequenceItem>.Shared.Return(source.buffer);
            }
            source.ReleaseCallbacks(); // [fork]

            source.version++;
            source.buffer = null;
            source.tail = 0;
            source.lastTail = 0;
            source.count = 0;
            source.duration = 0;

            pool.TryPush(source);
        }

        public ref MotionSequenceBuilderSource NextNode => ref next;
        public ushort Version => version;

        MotionSequenceBuilderSource next;
        ushort version;
        MotionSequenceItem[] buffer;
        int count;
        double tail;
        double lastTail;
        double duration;

        // [fork] Sequence callbacks (SequenceCallbackExtensions) and guarded children.
        MotionSequenceCallback[] callbackBuffer;
        int callbackCount;
        bool hasGuardedChildren;

        public void Append(MotionHandle handle)
        {
            MotionManager.AddToSequence(handle, out var motionDuration);
            AddItem(new MotionSequenceItem(tail, handle));
            OnChildAdded(handle); // [fork]
            AppendInterval(motionDuration);
        }

        public void AppendInterval(double interval)
        {
            lastTail = tail;
            tail += interval;
            duration = Math.Max(duration, tail);
        }

        public void Insert(double position, MotionHandle handle)
        {
            MotionManager.AddToSequence(handle, out var motionDuration);
            AddItem(new MotionSequenceItem(position, handle));
            OnChildAdded(handle); // [fork]
            duration = Math.Max(duration, position + motionDuration);
        }

        public void Join(MotionHandle handle)
        {
            Insert(lastTail, handle);
        }

        public MotionHandle Schedule(Action<MotionBuilder<double, NoOptions, DoubleMotionAdapter>> configuration)
        {
            var source = MotionSequenceSource.Rent();
            var builder = LMotion.Create(0.0, duration, (float)duration)
                .WithOnComplete(source.OnCompleteDelegate)
                .WithOnCancel(source.OnCancelDelegate);

            configuration?.Invoke(builder);
            var handle = builder.Bind(source, (x, source) => source.Time = x);

            source.Initialize(handle, buffer, count, duration);
            TransferCallbacks(source); // [fork]
            buffer = null;
            return handle;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void AddItem(in MotionSequenceItem item)
        {
            if (buffer == null)
            {
                buffer = ArrayPool<MotionSequenceItem>.Shared.Rent(32);
            }
            else if (buffer.Length == count)
            {
                var newBuffer = ArrayPool<MotionSequenceItem>.Shared.Rent(count * 2);
                buffer.CopyTo(newBuffer.AsSpan(0, count));
                ArrayPool<MotionSequenceItem>.Shared.Return(buffer);
                buffer = newBuffer;
            }

            buffer[count] = item;
            count++;
        }

        // [fork] Sequence callbacks and guarded children, down to the end of the class.
        public void AppendCallback(Action callback)
        {
            AddCallback(tail, callback);

            // Like Append/AppendInterval, the callback becomes the last added item, so a following Join()
            // is inserted at the callback position. The tail itself does not move.
            AppendInterval(0);
        }

        public void InsertCallback(double position, Action callback)
        {
            AddCallback(position, callback);
            duration = Math.Max(duration, position);
        }

        void AddCallback(double position, Action callback)
        {
            if (callbackBuffer == null)
            {
                callbackBuffer = ArrayPool<MotionSequenceCallback>.Shared.Rent(8);
            }
            else if (callbackBuffer.Length == callbackCount)
            {
                var newBuffer = ArrayPool<MotionSequenceCallback>.Shared.Rent(callbackCount * 2);
                callbackBuffer.AsSpan(0, callbackCount).CopyTo(newBuffer);
                MotionSequenceCallback.ReturnBuffer(callbackBuffer, callbackCount);
                callbackBuffer = newBuffer;
            }

            // Keep the buffer sorted by position. Callbacks at the same position keep the order they were added in.
            var index = callbackCount;
            while (index > 0 && callbackBuffer[index - 1].Position > position)
            {
                callbackBuffer[index] = callbackBuffer[index - 1];
                index--;
            }

            callbackBuffer[index] = new MotionSequenceCallback { Position = position, Callback = callback };
            callbackCount++;
        }

        // Called by Append()/Insert() once the child has been added: a sequence that contains a child using a fork extension
        // (OnStart, sequence callbacks) is evaluated with the same guards as that child.
        void OnChildAdded(MotionHandle handle)
        {
            if (MotionManager.GetManagedDataRef(handle, false).Extension.IsGuarded) hasGuardedChildren = true;
        }

        // Called by Schedule() after the sequence motion has been created. The source decides whether the sequence is guarded
        // (callbacks, a guarded child, or a guarded motion); a sequence that is not keeps the original behavior.
        void TransferCallbacks(MotionSequenceSource source)
        {
            source.SetCallbacks(callbackBuffer, callbackCount, hasGuardedChildren);
            callbackBuffer = null;
            callbackCount = 0;
            hasGuardedChildren = false;
        }

        void ReleaseCallbacks()
        {
            hasGuardedChildren = false;
            if (callbackBuffer == null) return;
            MotionSequenceCallback.ReturnBuffer(callbackBuffer, callbackCount);
            callbackBuffer = null;
            callbackCount = 0;
        }
    }

    public struct MotionSequenceBuilder : IDisposable
    {
        internal MotionSequenceBuilderSource source;
        internal ushort version;

        internal MotionSequenceBuilder(MotionSequenceBuilderSource source)
        {
            this.source = source;
            this.version = source.Version;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly MotionSequenceBuilder Append(MotionHandle handle)
        {
            CheckIsDisposed();
            source.Append(handle);
            return this;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly MotionSequenceBuilder AppendInterval(float interval)
        {
            CheckIsDisposed();
            source.AppendInterval(interval);
            return this;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly MotionSequenceBuilder Insert(float position, MotionHandle handle)
        {
            CheckIsDisposed();
            source.Insert(position, handle);
            return this;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly MotionSequenceBuilder Join(MotionHandle handle)
        {
            CheckIsDisposed();
            source.Join(handle);
            return this;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public MotionHandle Run()
        {
            return Run(null);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public MotionHandle Run(Action<MotionBuilder<double, NoOptions, DoubleMotionAdapter>> configuration)
        {
            CheckIsDisposed();
            var handle = source.Schedule(configuration);
            Dispose();
            return handle;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Dispose()
        {
            CheckIsDisposed();
            MotionSequenceBuilderSource.Return(source);
            source = null;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal readonly void CheckIsDisposed() // [fork] internal: also used by AppendCallback/InsertCallback
        {
            if (source == null || source.Version != version)
            {
                throw new InvalidOperationException("MotionSequenuceBuilder is either not initialized or has already run.");
            }
        }
    }
}