using System;
using System.Buffers;
using System.Collections.Generic;
using UnityEngine;

namespace LitMotion
{
    /// <summary>
    /// Maps targets (any object used as a key) to the motions registered with SetTarget().
    /// </summary>
    /// <remarks>
    /// <para>
    /// The target of a motion and the id of its registration are stored in the motion itself (<see cref="MotionExtensionData"/>).
    /// The registry keeps, per target, a linked list of (handle, registration id) entries in registration order, in a shared pool.
    /// An entry is valid while its motion is active and still has that registration (a new SetTarget() call, even to the same
    /// target again, is a new registration). Invalid entries are skipped and dropped.
    /// </para>
    /// <para>
    /// Lifetime of the entries (and of the references to the targets): an entry is released when its motion is removed from
    /// its storage (including motions of a sequence and storage resets). Entries whose registration ended otherwise are dropped
    /// when their target list is used again, or by a sweep over all targets, which runs when the number of targets has doubled
    /// since the previous one, when exiting play mode and after the first scene of a play session has loaded.
    /// </para>
    /// </remarks>
    internal static class MotionTargetRegistry
    {
        struct Entry
        {
            public MotionHandle Handle;
            public uint Registration;
            public int Next;
        }

        struct TargetList
        {
            public int Head;
            public int Tail;
            public int Count;
            public int PruneAt;
        }

        const int MinPruneCount = 8;
        const int MinSweepThreshold = 64;

        static readonly Dictionary<object, TargetList> targets = new();
        static Entry[] entries = new Entry[64];
        static int usedEntries;
        static int freeEntry = -1;
        static object[] sweepKeys = new object[64];
        static TargetList[] sweepLists = new TargetList[64];
        static int sweepThreshold = MinSweepThreshold;

        internal static int TargetCount => targets.Count;

        internal static int GetEntryCount(object target)
        {
            return targets.TryGetValue(target, out var list) ? list.Count : 0;
        }

        public static void Register(MotionHandle handle, object target)
        {
            ValidateTarget(target);

            // Throws if the handle is invalid or the motion is in a sequence.
            if (!MotionManager.IsValidHandle(handle)) Error.MotionNotExists();
            MotionManager.GetDataRef(handle);
            if (!MotionManager.IsActive(handle)) Error.MotionHasBeenCanceledOrCompleted();

            // Every call is a new registration, even to the same target (it moves to the end of the target's list).
            ref var extension = ref MotionManager.GetManagedDataRef(handle).Extension;
            if (extension.HasTarget) Remove(extension.Target, extension.Registration);

            var registration = extension.SetTarget(target);
            Add(target, handle, registration);
        }

        public static void Release(ref MotionExtensionData extension)
        {
            if (!extension.HasTarget) return;
            Remove(extension.Target, extension.Registration);
            extension.ClearTarget();
        }

        public static int Cancel(object target)
        {
            return CancelOrComplete(target, false);
        }

        public static int Complete(object target)
        {
            return CancelOrComplete(target, true);
        }

        // For tests: forgets every registration (the motions keep their target, so do not reuse them afterwards).
        public static void Clear()
        {
            targets.Clear();
            Array.Clear(entries, 0, usedEntries);
            usedEntries = 0;
            freeEntry = -1;
            sweepThreshold = MinSweepThreshold;
        }

        static int CancelOrComplete(object target, bool complete)
        {
            ValidateTarget(target);
            if (!targets.TryGetValue(target, out var list)) return 0;

            // The registrations of this call are those that exist now: registrations made by the callbacks
            // (including a motion registered again) are not affected.
            var snapshot = ArrayPool<Entry>.Shared.Rent(Math.Max(1, list.Count));
            var count = 0;
            for (var index = list.Head; index >= 0 && count < snapshot.Length; index = entries[index].Next)
            {
                snapshot[count++] = entries[index];
            }

            try
            {
                var result = 0;
                for (int i = 0; i < count; i++)
                {
                    var entry = snapshot[i];
                    if (!IsRegistered(entry, target)) continue;

                    ref var data = ref MotionManager.GetDataRef(entry.Handle, false);

                    // Motions in a sequence are controlled by the sequence.
                    if (data.State.IsInSequence) continue;

                    MotionStatus requested;
                    if (!complete || data.Parameters.Loops < 0)
                    {
                        // An infinitely looping motion cannot be completed: it is canceled.
                        requested = MotionStatus.Canceled;
                        MotionManager.TryCancel(entry.Handle);
                    }
                    else if (data.State.Status == MotionStatus.Completed)
                    {
                        // A preserved motion that has already completed is left as is.
                        continue;
                    }
                    else
                    {
                        requested = MotionStatus.Completed;
                        MotionManager.TryComplete(entry.Handle);
                    }

                    // The result of the call for this motion: it counts only if the motion is in the requested state once the
                    // call is done with it (its callbacks may have moved it back, or completed/canceled it themselves).
                    if (MotionManager.IsValid(entry.Handle) && MotionManager.GetDataRef(entry.Handle, false).State.Status == requested) result++;
                }

                // Drop the entries of the motions that ended; the others keep their place.
                if (targets.TryGetValue(target, out list))
                {
                    Prune(ref list, target);
                    Store(target, list);
                }

                return result;
            }
            finally
            {
                ArrayPool<Entry>.Shared.Return(snapshot);
            }
        }

        static bool IsRegistered(in Entry entry, object target)
        {
            if (!MotionManager.IsActive(entry.Handle)) return false;
            ref var extension = ref MotionManager.GetManagedDataRef(entry.Handle, false).Extension;
            return extension.Registration == entry.Registration && Equals(extension.Target, target);
        }

        static void Add(object target, MotionHandle handle, uint registration)
        {
            if (targets.TryGetValue(target, out var list))
            {
                if (list.Count >= list.PruneAt)
                {
                    Prune(ref list, target);
                    list.PruneAt = Math.Max(MinPruneCount, list.Count * 2);
                }
            }
            else
            {
                if (targets.Count >= sweepThreshold) Sweep();
                list = new TargetList { Head = -1, Tail = -1, PruneAt = MinPruneCount };
            }

            var entryIndex = AllocateEntry();
            entries[entryIndex] = new Entry { Handle = handle, Registration = registration, Next = -1 };
            if (list.Tail < 0) list.Head = entryIndex;
            else entries[list.Tail].Next = entryIndex;
            list.Tail = entryIndex;
            list.Count++;
            targets[target] = list;
        }

        static void Remove(object target, uint registration)
        {
            if (target == null || !targets.TryGetValue(target, out var list)) return;

            var previous = -1;
            for (var index = list.Head; index >= 0; index = entries[index].Next)
            {
                if (entries[index].Registration != registration)
                {
                    previous = index;
                    continue;
                }

                Unlink(ref list, previous, index);
                break;
            }

            Store(target, list);
        }

        static void Prune(ref TargetList list, object target)
        {
            var previous = -1;
            var index = list.Head;
            while (index >= 0)
            {
                var next = entries[index].Next;
                if (IsRegistered(entries[index], target)) previous = index;
                else Unlink(ref list, previous, index);
                index = next;
            }
        }

        static void Unlink(ref TargetList list, int previous, int index)
        {
            var next = entries[index].Next;
            if (previous < 0) list.Head = next;
            else entries[previous].Next = next;
            if (list.Tail == index) list.Tail = previous;
            FreeEntry(index);
            list.Count--;
        }

        static void Store(object target, in TargetList list)
        {
            if (list.Count == 0) targets.Remove(target);
            else targets[target] = list;
        }

        // Drops the entries that are no longer registered and rebuilds the dictionary. Rebuilding (instead of removing
        // keys) also copes with keys whose hash code changed while registered.
        static void Sweep()
        {
            var count = 0;
            foreach (var pair in targets)
            {
                var list = pair.Value;
                Prune(ref list, pair.Key);
                if (list.Count == 0) continue;

                if (count == sweepKeys.Length)
                {
                    Array.Resize(ref sweepKeys, count * 2);
                    Array.Resize(ref sweepLists, count * 2);
                }
                sweepKeys[count] = pair.Key;
                sweepLists[count] = list;
                count++;
            }

            targets.Clear();
            for (int i = 0; i < count; i++)
            {
                var key = sweepKeys[i];
                sweepKeys[i] = null;

                if (targets.ContainsKey(key))
                {
                    // Two keys became equal while registered: their entries cannot be told apart any more.
                    FreeList(sweepLists[i]);
                    continue;
                }

                targets.Add(key, sweepLists[i]);
            }

            sweepThreshold = Math.Max(MinSweepThreshold, targets.Count * 2);
        }

        static int AllocateEntry()
        {
            if (freeEntry >= 0)
            {
                var index = freeEntry;
                freeEntry = entries[index].Next;
                return index;
            }

            if (usedEntries == entries.Length) Array.Resize(ref entries, entries.Length * 2);
            return usedEntries++;
        }

        static void FreeEntry(int index)
        {
            entries[index] = new Entry { Next = freeEntry };
            freeEntry = index;
        }

        static void FreeList(TargetList list)
        {
            for (var index = list.Head; index >= 0;)
            {
                var next = entries[index].Next;
                FreeEntry(index);
                index = next;
            }
        }

        static void ValidateTarget(object target)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (target is MotionHandle) throw new ArgumentException("A MotionHandle cannot be used as a target. Use the methods of the MotionHandle itself instead.", nameof(target));
        }

        // Play mode transitions: the registry is not cleared, because motions can survive a transition
        // (ManualMotionDispatcher, motions played in edit mode). The entries of the motions that are gone are dropped.
        // The player-loop motions of a play session are discarded when the next one starts (MotionDispatcher clears
        // them before the first scene is loaded).
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void PruneAfterEnterPlayMode()
        {
            Sweep();
        }

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        static void RegisterPruneOnExitPlayMode()
        {
            UnityEditor.EditorApplication.playModeStateChanged += static state =>
            {
                if (state == UnityEditor.PlayModeStateChange.EnteredEditMode) Sweep();
            };
        }
#endif
    }
}
