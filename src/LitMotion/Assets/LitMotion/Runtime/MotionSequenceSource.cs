using System;
using System.Buffers;
using LitMotion.Collections;

namespace LitMotion
{
    internal struct MotionSequenceItem : IComparable<MotionSequenceItem>
    {
        public MotionSequenceItem(double position, MotionHandle handle)
        {
            Position = position;
            Handle = handle;
        }

        public double Position;
        public MotionHandle Handle;

        public int CompareTo(MotionSequenceItem other)
        {
            return Position.CompareTo(other.Position);
        }
    }

    // [fork] A callback of a sequence, see MotionSequenceSource.Callbacks.cs.
    internal struct MotionSequenceCallback
    {
        public double Position;
        public Action Callback;
        public double InvokedAt; // raw time of the sequence in the update that invoked the callback in the current loop

        public static void ReturnBuffer(MotionSequenceCallback[] buffer, int count)
        {
            if (buffer == null) return;
            buffer.AsSpan(0, count).Clear(); // do not keep the callbacks alive in the pool
            ArrayPool<MotionSequenceCallback>.Shared.Return(buffer);
        }
    }

    internal sealed partial class MotionSequenceSource : ILinkedPoolNode<MotionSequenceSource> // [fork] partial: the callbacks are in MotionSequenceSource.Callbacks.cs
    {
        static LinkedPool<MotionSequenceSource> pool;

        public static MotionSequenceSource Rent()
        {
            if (!pool.TryPop(out var result)) result = new();
            return result;
        }

        public static void Return(MotionSequenceSource source)
        {
            if (source.itemBuffer != null)
            {
                ArrayPool<MotionSequenceItem>.Shared.Return(source.itemBuffer);
                source.itemBuffer = null;
            }
            source.ReleaseCallbacks(); // [fork]

            source.itemCount = default;
            source.duration = default;
            source.time = default;

            pool.TryPush(source);
        }

        public void Initialize(MotionHandle handle, MotionSequenceItem[] itemBuffer, int itemCount, double duration)
        {
            this.handle = handle;
            this.itemCount = itemCount;
            this.itemBuffer = itemBuffer;
            this.duration = duration;
            this.time = 0;

            if (itemBuffer != null) Array.Sort(itemBuffer, 0, itemCount);
        }

        MotionSequenceSource()
        {
            onCompleteDelegate = OnComplete;
            onCancelDelegate = OnCancel;
        }

        readonly Action onCompleteDelegate;
        readonly Action onCancelDelegate;
        MotionSequenceSource next;

        MotionHandle handle;
        MotionSequenceItem[] itemBuffer;
        int itemCount;
        double duration;
        double time;

        // [fork] Sequence callbacks, see MotionSequenceSource.Callbacks.cs.
        MotionSequenceCallback[] callbackBuffer;
        int callbackCount;
        bool hasGuardedChildren;
        LoopType loopType;
        bool isGuarded;
        int version;           // incremented when the source is returned to the pool
        Action userOnComplete; // the user's OnComplete/OnCancel of a guarded sequence, invoked after its children are released
        Action userOnCancel;

        // Callback playhead: the loop, the position in the loop and the callbacks of the loop already invoked.
        long playheadLoop;
        CallbackPhase playheadPhase;
        double playheadLegPosition; // negated in mirrored loops, so that "after" is always "greater"
        int invokedCount;
        double lastUpdateTime;

        // The update in progress and its target.
        int updateVersion;     // incremented on every update and release
        int openUpdateVersion; // 0 when no update is in progress
        int targetInvokedCount;
        bool interleaveCallbacks;
        double targetTime;
        CallbackLocation targetLocation;

        public ref MotionSequenceSource NextNode => ref next;

        public Action OnCompleteDelegate => onCompleteDelegate;
        public Action OnCancelDelegate => onCancelDelegate;

        public ReadOnlySpan<MotionSequenceItem> Items => itemBuffer.AsSpan(0, itemCount);

        public double Time
        {
            get => time;
            set
            {
                if (!BeginCallbacks(out var update)) return; // [fork]
                try // [fork]
                {
                    time = value;

                    var span = Items;
                    var index = span.Length - 1;
                    while (index >= 0)
                    {
                        var item = span[index];
                        if (item.Position < time) break;
                        if (item.Position == time && isGuarded) break; // [fork] a child that starts exactly here starts after the callbacks here
                        if (!IsCurrent(update)) return; // [fork]
                        MotionManager.SetTime(item.Handle, time - item.Position, false);
                        index--;
                    }

                    foreach (var item in span[..(index + 1)])
                    {
                        if (!InvokeCallbacks(item.Position, update)) return; // [fork]
                        MotionManager.SetTime(item.Handle, time - item.Position, false);
                    }

                    EndCallbacks(update); // [fork]
                }
                finally
                {
                    AbortCallbacks(update); // [fork]
                }
            }
        }

        public double Duration => duration;

        void OnComplete()
        {
            if (!handle.IsValid()) return;
            var onComplete = userOnComplete; // [fork] OnComplete of the user of a guarded sequence (null otherwise)
            if (MotionManager.GetDataRef(handle, false).State.IsPreserved)
            {
                onComplete?.Invoke(); // [fork]
                return;
            }

            var release = BeginRelease(); // [fork]
            foreach (var item in Items)
            {
                MotionManager.Cancel(item.Handle, false);
                if (!IsCurrent(release)) return; // [fork] a child callback moved the sequence: no release, no OnComplete
            }

            Return(this);
            onComplete?.Invoke(); // [fork]
        }

        void OnCancel()
        {
            var onCancel = userOnCancel; // [fork] OnCancel of the user of a guarded sequence (null otherwise)
            var release = BeginRelease(); // [fork]
            foreach (var item in Items)
            {
                MotionManager.Cancel(item.Handle, false);
                if (!IsCurrent(release)) return; // [fork] a child callback moved the sequence: no release, no OnCancel
            }

            Return(this);
            onCancel?.Invoke(); // [fork]
        }
    }
}