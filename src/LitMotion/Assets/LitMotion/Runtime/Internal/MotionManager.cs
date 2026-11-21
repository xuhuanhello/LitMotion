using System;
using System.Runtime.CompilerServices;
using LitMotion.Collections;

namespace LitMotion
{
    internal static class MotionManager
    {
        static FastListCore<IMotionStorage> list;

        public static int MotionTypeCount { get; private set; }

        public static void Register<TValue, TOptions, TAdapter>(MotionStorage<TValue, TOptions, TAdapter> storage)
            where TValue : unmanaged
            where TOptions : unmanaged, IMotionOptions
            where TAdapter : unmanaged, IMotionAdapter<TValue, TOptions>
        {
            list.Add(storage);
            MotionTypeCount++;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref MotionData GetDataRef(in MotionHandle handle, bool checkIsInSequence = true) // [fork] avoid copying the handle on each lookup
        {
            CheckTypeId(handle);
            return ref list[handle.StorageId].GetDataRef(handle, checkIsInSequence);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref ManagedMotionData GetManagedDataRef(MotionHandle handle, bool checkIsInSequence = true)
        {
            CheckTypeId(handle);
            return ref list[handle.StorageId].GetManagedDataRef(handle, checkIsInSequence);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static MotionDebugInfo GetDebugInfo(MotionHandle handle)
        {
            CheckTypeId(handle);
            return list[handle.StorageId].GetDebugInfo(handle);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Complete(MotionHandle handle, bool checkIsInSequence = true)
        {
            CheckTypeId(handle);
            list[handle.StorageId].Complete(handle, checkIsInSequence);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryComplete(MotionHandle handle, bool checkIsInSequence = true)
        {
            if (handle.StorageId < 0 || handle.StorageId >= MotionTypeCount) return false;
            return list[handle.StorageId].TryComplete(handle, checkIsInSequence);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Cancel(MotionHandle handle, bool checkIsInSequence = true)
        {
            CheckTypeId(handle);
            list[handle.StorageId].Cancel(handle, checkIsInSequence);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryCancel(MotionHandle handle, bool checkIsInSequence = true)
        {
            if (handle.StorageId < 0 || handle.StorageId >= MotionTypeCount) return false;
            return list[handle.StorageId].TryCancel(handle, checkIsInSequence);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsValid(MotionHandle handle)
        {
            if (handle.StorageId < 0 || handle.StorageId >= MotionTypeCount) return false;
            return list[handle.StorageId].IsValid(handle);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsActive(MotionHandle handle)
        {
            if (handle.StorageId < 0 || handle.StorageId >= MotionTypeCount) return false;
            return list[handle.StorageId].IsActive(handle);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsPlaying(MotionHandle handle)
        {
            if (handle.StorageId < 0 || handle.StorageId >= MotionTypeCount) return false;
            return list[handle.StorageId].IsPlaying(handle);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetTime(MotionHandle handle, double time, bool checkIsInSequence = true)
        {
            CheckTypeId(handle);
            list[handle.StorageId].SetTime(handle, time, checkIsInSequence);
        }

        // [fork] IsValid for the public entry points of the fork: also false, instead of an exception, for a handle whose index
        // is out of the range of the slots. (IsValid itself keeps the behavior of v2.0.2.)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsValidHandle(MotionHandle handle)
        {
            if (handle.StorageId < 0 || handle.StorageId >= MotionTypeCount) return false;
            var storage = list[handle.StorageId];
            return storage.HasSlot(handle.Index) && storage.IsValid(handle);
        }

        // [fork] See MotionStorage.StopAtStart.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void StopAtStart(MotionHandle handle)
        {
            CheckTypeId(handle);
            list[handle.StorageId].StopAtStart(handle);
        }

        // [fork] See MotionEvaluation.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static MotionEvaluation CaptureEvaluation(MotionHandle handle)
        {
            CheckTypeId(handle);
            return list[handle.StorageId].CaptureEvaluation(handle);
        }

        // [fork] See MotionEvaluation.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsCurrent(in MotionEvaluation evaluation)
        {
            if (!evaluation.IsGuarded) return true;
            if (evaluation.StorageId < 0 || evaluation.StorageId >= MotionTypeCount) return false;
            return list[evaluation.StorageId].IsCurrent(evaluation);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void AddToSequence(MotionHandle handle, out double motionDuration)
        {
            CheckTypeId(handle);
            list[handle.StorageId].AddToSequence(handle, out motionDuration);
        }

        // For MotionTracker
        public static (Type ValueType, Type OptionsType, Type AdapterType) GetMotionType(MotionHandle handle)
        {
            CheckTypeId(handle);
            var storageType = list[handle.StorageId].GetType();
            var valueType = storageType.GenericTypeArguments[0];
            var optionsType = storageType.GenericTypeArguments[1];
            var adapterType = storageType.GenericTypeArguments[2];
            return (valueType, optionsType, adapterType);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void CheckTypeId(in MotionHandle handle)
        {
            if (handle.StorageId < 0 || handle.StorageId >= MotionTypeCount)
            {
                throw new ArgumentException("Invalid type id.");
            }
        }
    }
}