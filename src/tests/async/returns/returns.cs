// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Xunit;

public class Async2Returns
{
    [Fact]
    public static void TestEntryPoint()
    {
        Returns(new C()).Wait();
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(17, false)]
    [InlineData(17, true)]
    [InlineData(-91, false)]
    [InlineData(-91, true)]
    public static void ResumptionPreservesState(int seed, bool suspend)
    {
        S<long> result = ResumeWithState(seed, suspend).GetAwaiter().GetResult();

        Assert.Equal(seed + 3L, result.A);
        Assert.Equal(seed * 7L + 11, result.B);
        Assert.Equal(seed * 13L + 19, result.C);
        Assert.Equal(seed * 23L + 29, result.D);
    }

    [Theory]
    [InlineData("first")]
    [InlineData("another result")]
    public static void ResumptionPropagatesGcStruct(string value)
    {
        S<string> result = ResumeGcStruct(value).GetAwaiter().GetResult();
        Assert.Same(value, result.A);
        Assert.Equal(value, result.B);
        Assert.Same(result.B, result.C);
        Assert.Same(value, result.D);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<S<string>> ResumeGcStruct(string value)
    {
        string copy = new string(value.ToCharArray());
        await Task.Yield();
        CollectWithWrapperOnStack();
        return new S<string> { A = value, B = copy, C = copy, D = value };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CollectWithWrapperOnStack()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<S<long>> ResumeWithState(int seed, bool suspend)
    {
        long a = seed + 3L;
        long b = seed * 7L + 11;
        long c = seed * 13L + 19;
        long d = seed * 23L + 29;
        double fraction = seed + 0.25;
        string text = seed.ToString(System.Globalization.CultureInfo.InvariantCulture);
        C holder = new C { Val = new S<long> { A = a, B = b, C = c, D = d } };

        for (int i = 0; i < 3; i++)
        {
            if (suspend)
            {
                await Task.Yield();
            }
            else
            {
                await Task.CompletedTask;
            }

            Assert.Equal(a, holder.Val.A);
            Assert.Equal(b, holder.Val.B);
            Assert.Equal(c, holder.Val.C);
            Assert.Equal(d, holder.Val.D);
            Assert.Equal(seed + 0.25, fraction);
            Assert.Equal(seed.ToString(System.Globalization.CultureInfo.InvariantCulture), text);

            if ((seed & 1) == 0)
            {
                await Task.Yield();
                a += i;
            }
            else
            {
                await Task.Yield();
                b += i;
            }

            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

            Assert.Equal(seed + 3L, holder.Val.A);
            Assert.Equal(seed * 7L + 11, holder.Val.B);
            if ((seed & 1) == 0)
            {
                a -= i;
            }
            else
            {
                b -= i;
            }
        }

        GC.KeepAlive(text);
        return new S<long> { A = a, B = b, C = c, D = d };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task Returns(C c)
    {
        int count = TestLibrary.Utilities.IsCoreClrInterpreter ? 200 : 20000;
        for (int i = 0; i < count; i++)
        {
            S<long> val = await ReturnsStruct();

            AssertEqual(42, val.A);
            AssertEqual(4242, val.B);
            AssertEqual(424242, val.C);
            AssertEqual(42424242, val.D);

            c.Val = default;
            c.Val = await ReturnsStruct();

            AssertEqual(42, c.Val.A);
            AssertEqual(4242, c.Val.B);
            AssertEqual(424242, c.Val.C);
            AssertEqual(42424242, c.Val.D);

            S<string> strings = await ReturnsStructGC();
            AssertEqual("A", strings.A);
            AssertEqual("B", strings.B);
            AssertEqual("C", strings.C);
            AssertEqual("D", strings.D);

            S<byte> bytes = await ReturnsBytes();
            AssertEqual(4, bytes.A);
            AssertEqual(40, bytes.B);
            AssertEqual(42, bytes.C);
            AssertEqual(45, bytes.D);

            string str = await ReturnsString();
            AssertEqual("a string!", str);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertEqual<T>(T expected, T actual)
    {
        Assert.Equal(expected, actual);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<S<long>> ReturnsStruct()
    {
        await Task.Yield();
        return new S<long> { A = 42, B = 4242, C = 424242, D = 42424242 };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<S<string>> ReturnsStructGC()
    {
        await Task.Yield();
        return new S<string> { A = "A", B = "B", C = "C", D = "D" };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<S<byte>> ReturnsBytes()
    {
        await Task.Yield();
        return new S<byte> { A = 4, B = 40, C = 42, D = 45 };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<string> ReturnsString()
    {
        await Task.Yield();
        return "a string!";
    }

    private struct S<T> { public T A, B, C, D; }

    private class C { public S<long> Val; }
}
