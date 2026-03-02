// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace System.Runtime.CompilerServices
{
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static partial class AsyncHelpers
    {
#if CORECLR || NATIVEAOT
        // "BypassReadyToRun" is until AOT/R2R typesystem has support for MethodImpl.Async
        // Must be NoInlining because we use AsyncSuspend to manufacture an explicit suspension point.
        // It will not capture/restore any local state that is live across it.

        /// <summary>
        /// Awaits the specified awaiter and returns when the awaiter has completed.
        /// </summary>
        /// <typeparam name="TAwaiter">The awaiter type.</typeparam>
        /// <param name="awaiter">The awaiter to await.</param>
        [BypassReadyToRun]
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.Async)]
        [StackTraceHidden]
        public static unsafe void AwaitAwaiter<TAwaiter>(TAwaiter awaiter) where TAwaiter : INotifyCompletion
        {
            ref RuntimeAsyncAwaitState state = ref t_runtimeAsyncAwaitState;
            Continuation? sentinelContinuation = state.SentinelContinuation;
            if (sentinelContinuation == null)
                state.SentinelContinuation = sentinelContinuation = new Continuation();

            state.Notifier = awaiter;
            state.CaptureContexts();
            state.OnCompleted = &CallOnCompletedGenericNotifier;
            AsyncSuspend(sentinelContinuation);
        }

        private static void CallOnCompletedGenericNotifier(ref RuntimeAsyncAwaitState state, Continuation headContinuation, Task task)
        {
            ExecutionContext? execCtx = state.ExecutionContext;
            SynchronizationContext? syncCtx = state.SynchronizationContext;
            INotifyCompletion? notifier = state.Notifier;
            Debug.Assert(notifier != null && task.m_action is Action);

            state.ExecutionContext = null;
            state.SynchronizationContext = null;
            state.Notifier = null;

            notifier.OnCompleted(Unsafe.As<Delegate, Action>(ref task.m_action));
        }

        // Must be NoInlining because we use AsyncSuspend to manufacture an explicit suspension point.
        // It will not capture/restore any local state that is live across it.

        /// <summary>
        /// Awaits the specified awaiter without capturing the execution context and returns when the awaiter has completed.
        /// </summary>
        /// <typeparam name="TAwaiter">The awaiter type.</typeparam>
        /// <param name="awaiter">The awaiter to await.</param>
        [BypassReadyToRun]
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.Async)]
        [StackTraceHidden]
        public static unsafe void UnsafeAwaitAwaiter<TAwaiter>(TAwaiter awaiter) where TAwaiter : ICriticalNotifyCompletion
        {
            ref RuntimeAsyncAwaitState state = ref t_runtimeAsyncAwaitState;
            Continuation? sentinelContinuation = state.SentinelContinuation;
            if (sentinelContinuation == null)
                state.SentinelContinuation = sentinelContinuation = new Continuation();

            state.CriticalNotifier = awaiter;
            state.CaptureContexts();
            state.OnCompleted = &CallOnCompletedGenericCriticalNotifier;
            AsyncSuspend(sentinelContinuation);
        }

        private static void CallOnCompletedGenericCriticalNotifier(ref RuntimeAsyncAwaitState state, Continuation headContinuation, Task task)
        {
            ExecutionContext? execCtx = state.ExecutionContext;
            SynchronizationContext? syncCtx = state.SynchronizationContext;
            ICriticalNotifyCompletion? notifier = state.CriticalNotifier;
            Debug.Assert(notifier != null && task.m_action is Action);

            state.ExecutionContext = null;
            state.SynchronizationContext = null;
            state.CriticalNotifier = null;

            notifier.UnsafeOnCompleted(Unsafe.As<Delegate, Action>(ref task.m_action));
        }

        /// <summary>
        /// Awaits the specified <see cref="Task{T}"/> and returns its result, throwing any exception produced by the task.
        /// </summary>
        /// <typeparam name="T">The result type produced by the task.</typeparam>
        /// <param name="task">The task to await.</param>
        [Intrinsic]
        [BypassReadyToRun]
        [MethodImpl(MethodImplOptions.Async)]
        [StackTraceHidden]
        public static T Await<T>(Task<T> task)
        {
            TaskAwaiter<T> awaiter = task.GetAwaiter();
            if (!awaiter.IsCompleted)
            {
                UnsafeAwaitAwaiter(awaiter);
            }

            return awaiter.GetResult();
        }

        /// <summary>
        /// Awaits the specified <see cref="ValueTask"/> and throws any exception produced by the operation.
        /// </summary>
        /// <param name="task">The value task to await.</param>
        [Intrinsic]
        [BypassReadyToRun]
        [MethodImpl(MethodImplOptions.Async)]
        [StackTraceHidden]
        public static void Await(Task task)
        {
            TaskAwaiter awaiter = task.GetAwaiter();
            if (!awaiter.IsCompleted)
            {
                UnsafeAwaitAwaiter(awaiter);
            }

            awaiter.GetResult();
        }

        /// <summary>
        /// Awaits the specified <see cref="ValueTask{T}"/> and returns its result, throwing any exception produced by the operation.
        /// </summary>
        /// <typeparam name="T">The result type produced by the value task.</typeparam>
        /// <param name="task">The value task to await.</param>
        [Intrinsic]
        [BypassReadyToRun]
        [MethodImpl(MethodImplOptions.Async)]
        [StackTraceHidden]
        public static T Await<T>(ValueTask<T> task)
        {
            ValueTaskAwaiter<T> awaiter = task.GetAwaiter();
            if (!awaiter.IsCompleted)
            {
                UnsafeAwaitAwaiter(awaiter);
            }

            return awaiter.GetResult();
        }

        /// <summary>
        /// Awaits the specified <see cref="Task"/> and throws any exception produced by the task.
        /// </summary>
        /// <param name="task">The task to await.</param>
        [Intrinsic]
        [BypassReadyToRun]
        [MethodImpl(MethodImplOptions.Async)]
        [StackTraceHidden]
        public static void Await(ValueTask task)
        {
            ValueTaskAwaiter awaiter = task.GetAwaiter();
            if (!awaiter.IsCompleted)
            {
                UnsafeAwaitAwaiter(awaiter);
            }

            awaiter.GetResult();
        }

        /// <summary>
        /// Awaits the specified configured task awaitable without capturing the execution context and throws any exception produced by the operation.
        /// </summary>
        /// <param name="configuredAwaitable">The configured awaitable to await.</param>
        [Intrinsic]
        [BypassReadyToRun]
        [MethodImpl(MethodImplOptions.Async)]
        [StackTraceHidden]
        public static void Await(ConfiguredTaskAwaitable configuredAwaitable)
        {
            ConfiguredTaskAwaitable.ConfiguredTaskAwaiter awaiter = configuredAwaitable.GetAwaiter();
            if (!awaiter.IsCompleted)
            {
                UnsafeAwaitAwaiter(awaiter);
            }

            awaiter.GetResult();
        }

        /// <summary>
        /// Awaits the specified configured value task awaitable without capturing the execution context and throws any exception produced by the operation.
        /// </summary>
        /// <param name="configuredAwaitable">The configured value task awaitable to await.</param>
        [Intrinsic]
        [BypassReadyToRun]
        [MethodImpl(MethodImplOptions.Async)]
        [StackTraceHidden]
        public static void Await(ConfiguredValueTaskAwaitable configuredAwaitable)
        {
            ConfiguredValueTaskAwaitable.ConfiguredValueTaskAwaiter awaiter = configuredAwaitable.GetAwaiter();
            if (!awaiter.IsCompleted)
            {
                UnsafeAwaitAwaiter(awaiter);
            }

            awaiter.GetResult();
        }

        /// <summary>
        /// Awaits the specified configured task awaitable and returns its result, throwing any exception produced by the operation.
        /// </summary>
        /// <typeparam name="T">The result type produced by the awaitable.</typeparam>
        /// <param name="configuredAwaitable">The configured awaitable to await.</param>
        [Intrinsic]
        [BypassReadyToRun]
        [MethodImpl(MethodImplOptions.Async)]
        [StackTraceHidden]
        public static T Await<T>(ConfiguredTaskAwaitable<T> configuredAwaitable)
        {
            ConfiguredTaskAwaitable<T>.ConfiguredTaskAwaiter awaiter = configuredAwaitable.GetAwaiter();
            if (!awaiter.IsCompleted)
            {
                UnsafeAwaitAwaiter(awaiter);
            }

            return awaiter.GetResult();
        }

        /// <summary>
        /// Awaits the specified configured value task awaitable and returns its result, throwing any exception produced by the operation.
        /// </summary>
        /// <typeparam name="T">The result type produced by the awaitable.</typeparam>
        /// <param name="configuredAwaitable">The configured awaitable to await.</param>
        [Intrinsic]
        [BypassReadyToRun]
        [MethodImpl(MethodImplOptions.Async)]
        [StackTraceHidden]
        public static T Await<T>(ConfiguredValueTaskAwaitable<T> configuredAwaitable)
        {
            ConfiguredValueTaskAwaitable<T>.ConfiguredValueTaskAwaiter awaiter = configuredAwaitable.GetAwaiter();
            if (!awaiter.IsCompleted)
            {
                UnsafeAwaitAwaiter(awaiter);
            }

            return awaiter.GetResult();
        }

        [Intrinsic]
        [BypassReadyToRun]
        [MethodImpl(MethodImplOptions.Async)]
        [StackTraceHidden]
        internal static unsafe void UnsafeAwaitAwaiterInContinuation<T>(int offset) where T : ICriticalNotifyCompletion
        {
            ref RuntimeAsyncAwaitState state = ref t_runtimeAsyncAwaitState;
            Continuation? sentinelContinuation = state.SentinelContinuation;
            if (sentinelContinuation == null)
                state.SentinelContinuation = sentinelContinuation = new Continuation();

            state.NotifierOffset = offset;
            state.OnCompleted = default(T) is null ? &CallOnCompletedOnCriticalNotifier : &CallOnCompletedOnCriticalNotifier<T>;
            AsyncSuspend(sentinelContinuation);
        }

        [Intrinsic]
        [BypassReadyToRun]
        [MethodImpl(MethodImplOptions.Async)]
        [StackTraceHidden]
        internal static unsafe void AwaitAwaiterInContinuation<T>(int offset) where T : INotifyCompletion
        {
            ref RuntimeAsyncAwaitState state = ref t_runtimeAsyncAwaitState;
            Continuation? sentinelContinuation = state.SentinelContinuation;
            if (sentinelContinuation == null)
                state.SentinelContinuation = sentinelContinuation = new Continuation();

            state.NotifierOffset = offset;
            state.OnCompleted = default(T) is null ? &CallOnCompletedOnNotifier : &CallOnCompletedOnNotifier<T>;
            AsyncSuspend(sentinelContinuation);
        }

        private static void CallOnCompletedOnCriticalNotifier(ref RuntimeAsyncAwaitState state, Continuation headContinuation, Task task)
        {
            ref object notifierStorage = ref Unsafe.As<byte, object>(ref Unsafe.Add(ref RuntimeHelpers.GetRawData(headContinuation), state.NotifierOffset));
            Debug.Assert(notifierStorage is ICriticalNotifyCompletion && task.m_action is Action);
            ref ICriticalNotifyCompletion notifier = ref Unsafe.As<object, ICriticalNotifyCompletion>(ref notifierStorage);
            notifier.UnsafeOnCompleted(Unsafe.As<Delegate, Action>(ref task.m_action));
        }

        private static void CallOnCompletedOnNotifier(ref RuntimeAsyncAwaitState state, Continuation headContinuation, Task task)
        {
            ref object notifierStorage = ref Unsafe.As<byte, object>(ref Unsafe.Add(ref RuntimeHelpers.GetRawData(headContinuation), state.NotifierOffset));
            Debug.Assert(notifierStorage is INotifyCompletion && task.m_action is Action);
            ref INotifyCompletion notifier = ref Unsafe.As<object, INotifyCompletion>(ref notifierStorage);
            notifier.OnCompleted(Unsafe.As<Delegate, Action>(ref task.m_action));
        }

        private static void CallOnCompletedOnCriticalNotifier<T>(ref RuntimeAsyncAwaitState state, Continuation headContinuation, Task task) where T : ICriticalNotifyCompletion
        {
            ref T notifierStorage = ref Unsafe.As<byte, T>(ref Unsafe.Add(ref RuntimeHelpers.GetRawData(headContinuation), state.NotifierOffset));
            Debug.Assert(task.m_action is Action);
            T notifier = notifierStorage; // We need a copy here to have same semantics as C# compiler and the non-optimized case
            notifier.UnsafeOnCompleted(Unsafe.As<Delegate, Action>(ref task.m_action));
        }

        private static void CallOnCompletedOnNotifier<T>(ref RuntimeAsyncAwaitState state, Continuation headContinuation, Task task) where T : INotifyCompletion
        {
            ref T notifierStorage = ref Unsafe.As<byte, T>(ref Unsafe.Add(ref RuntimeHelpers.GetRawData(headContinuation), state.NotifierOffset));
            Debug.Assert(task.m_action is Action);
            T notifier = notifierStorage; // We need a copy here to have same semantics as C# compiler and the non-optimized case
            notifier.OnCompleted(Unsafe.As<Delegate, Action>(ref task.m_action));
        }

#else
        public static void UnsafeAwaitAwaiter<TAwaiter>(TAwaiter awaiter) where TAwaiter : ICriticalNotifyCompletion { throw new PlatformNotSupportedException("Runtime Async is not supported on this platform."); }
        public static void AwaitAwaiter<TAwaiter>(TAwaiter awaiter) where TAwaiter : INotifyCompletion { throw new PlatformNotSupportedException("Runtime Async is not supported on this platform."); }
        public static void Await(System.Threading.Tasks.Task task) { throw new PlatformNotSupportedException("Runtime Async is not supported on this platform."); }
        public static T Await<T>(System.Threading.Tasks.Task<T> task) { throw new PlatformNotSupportedException("Runtime Async is not supported on this platform."); }
        public static void Await(System.Threading.Tasks.ValueTask task) { throw new PlatformNotSupportedException("Runtime Async is not supported on this platform."); }
        public static T Await<T>(System.Threading.Tasks.ValueTask<T> task) { throw new PlatformNotSupportedException("Runtime Async is not supported on this platform."); }
        public static void Await(System.Runtime.CompilerServices.ConfiguredTaskAwaitable configuredAwaitable) { throw new PlatformNotSupportedException("Runtime Async is not supported on this platform."); }
        public static void Await(System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable configuredAwaitable) { throw new PlatformNotSupportedException("Runtime Async is not supported on this platform."); }
        public static T Await<T>(System.Runtime.CompilerServices.ConfiguredTaskAwaitable<T> configuredAwaitable) { throw new PlatformNotSupportedException("Runtime Async is not supported on this platform."); }
        public static T Await<T>(System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable<T> configuredAwaitable) { throw new PlatformNotSupportedException("Runtime Async is not supported on this platform."); }
#endif
    }
}
