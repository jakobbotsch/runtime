// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Diagnostics;
using Xunit;

public class Async2FibonacceWithYields
{
    internal static async Task<int> B(int n)
    {
        int num = 1;
        await Task.Yield();

        num *= 10;
        await Task.Yield();

        num *= 10;
        await Task.Yield();

        return num;
    }

    internal static async Task<int> A(int n)
    {
        int num = n;
        for (int num2 = 0; num2 < n; num2++)
        {
            num = await B(num);
        }

        return num;
    }

    [System.Runtime.CompilerServices.RuntimeAsyncMethodGeneration(false)]
    private static async Task<int> AsyncEntry()
    {
        int result = 0;
        for (int i = 0; i < 10; i++)
        {
            result = await A(100);
        }

        return result;
    }

    [Fact]
    public static int Test()
    {
        return AsyncEntry().Result;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(17)]
    public static void ResumptionJoinsSynchronousFlow(int seed)
    {
        Assert.Equal(seed * 100L + 31, ResumeAndJoin(seed, false).GetAwaiter().GetResult());
        Assert.Equal(seed * 100L + 31, ResumeAndJoin(seed, true).GetAwaiter().GetResult());
    }

    [Fact]
    public static void ResumptionWithDiscardedResult()
    {
        DiscardResult().GetAwaiter().GetResult();
    }

    [Theory]
    [InlineData(37)]
    [InlineData(-123)]
    public static void ResumptionPreservesReferences(int seed)
    {
        int expected = seed + seed.ToString(System.Globalization.CultureInfo.InvariantCulture).Length;
        Assert.Equal(expected, ResumeWithReferences(seed).GetAwaiter().GetResult());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static void ResumptionPropagatesTaskCompletion(bool fail)
    {
        TaskCompletionSource<int> source = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> result = ResumeAwaitTask(source.Task);
        Assert.False(result.IsCompleted);

        if (fail)
        {
            InvalidOperationException error = new InvalidOperationException("Expected task failure");
            source.SetException(error);
            Assert.Same(error, Assert.Throws<InvalidOperationException>(() => result.GetAwaiter().GetResult()));
        }
        else
        {
            source.SetResult(42);
            Assert.Equal(49, result.GetAwaiter().GetResult());
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(19)]
    public static void ResumptionRestoresStackArguments(int seed)
    {
        long result = ResumeStackArguments(seed, 2, 3, 5, 7, 11, new object(), "live").GetAwaiter().GetResult();
        Assert.Equal(seed + 32L, result);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<long> ResumeStackArguments(
        long a, long b, long c, long d, long e, long f, object beforeOnly, string after)
    {
        GC.KeepAlive(beforeOnly);
        await Task.Yield();
        CollectAfterResumption();
        long sum = a + b + c + d + e + f;
        await Task.Yield();
        CollectAfterResumption();
        return sum + after.Length;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<int> ResumeAwaitTask(Task<int> source)
    {
        int value = await source;
        return value + 7;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<int> ResumeWithReferences(int seed)
    {
        CapturedState state = new CapturedState
        {
            Value = seed,
            Text = seed.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };

        await Task.Yield();
        CollectAfterResumption();
        Assert.Equal(seed, state.Value);
        state.Value += state.Text.Length;

        await Task.Yield();
        CollectAfterResumption();
        return state.Value;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CollectAfterResumption()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    private class CapturedState
    {
        public int Value;
        public string Text;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task DiscardResult()
    {
        await ResumeAndJoin(17, true);
        await Task.Yield();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<long> ResumeAndJoin(int seed, bool suspend)
    {
        long result = seed * 100L;
        if (suspend)
        {
            await Task.Yield();
        }

        result += 7;
        if ((seed & 1) != 0)
        {
            result += 11;
            await Task.Yield();
        }
        else
        {
            await Task.Yield();
            result += 11;
        }

        result += 13;
        return result;
    }
}
