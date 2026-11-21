using System;

namespace LitMotion
{
    /// <summary>
    /// The OnStart callbacks of a motion. One WithOnStart() call is stored as the delegate itself; several calls are stored
    /// in a list (whose bound delegate is the stored value), so that the dispatch can invoke them one by one and stop when one
    /// of them supersedes the evaluation (e.g. cancels the motion). A delegate combined by the caller counts as one callback.
    /// </summary>
    internal sealed class MotionStartCallbacks
    {
        Action[] callbacks = new Action[4];
        int count;
        readonly Action invokeAll;

        MotionStartCallbacks()
        {
            invokeAll = InvokeAll;
        }

        public static Action Add(Action current, Action callback)
        {
            if (callback == null) return current;
            if (current == null) return callback;

            if (current.Target is not MotionStartCallbacks list || current != list.invokeAll)
            {
                list = new MotionStartCallbacks();
                list.Append(current);
            }

            list.Append(callback);
            return list.invokeAll;
        }

        public static int GetCount(Action onStart)
        {
            if (onStart == null) return 0;
            return onStart.Target is MotionStartCallbacks list && onStart == list.invokeAll ? list.count : 1;
        }

        public static Action GetAt(Action onStart, int index)
        {
            return onStart.Target is MotionStartCallbacks list && onStart == list.invokeAll ? list.callbacks[index] : onStart;
        }

        void Append(Action callback)
        {
            if (count == callbacks.Length) Array.Resize(ref callbacks, count * 2);
            callbacks[count++] = callback;
        }

        void InvokeAll()
        {
            for (int i = 0; i < count; i++) callbacks[i].Invoke();
        }
    }
}
