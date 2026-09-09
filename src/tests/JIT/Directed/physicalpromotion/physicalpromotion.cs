// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//

namespace JitTest_Directed_physicalpromotion_physicalpromotion;

using System.Runtime.CompilerServices;
using System;
using Xunit;
using System.Runtime.InteropServices;

public class PhysicalPromotion
{
    [Fact]
    public static void PartialOverlap1()
    {
        S s = default;
        s.A = 0x10101010;
        s.B = 0x20202020;

        Unsafe.InitBlockUnaligned(ref Unsafe.As<uint, byte>(ref s.C), 0xcc, 4);
        Assert.Equal(0xcccc1010U, s.A);
        Assert.Equal(0x2020ccccU, s.B);
    }

    private static S s_static = new S { A = 0x10101010, B = 0x20202020 };
    [Fact]
    public static void CopyFromLocalVar()
    {
        S src = s_static;
        S dst;
        dst = src;
        dst.A = dst.B + 3;
        dst.B = 0x20202020;
        Consume(dst);
        Assert.Equal(0x20202023U, dst.A);
        Assert.Equal(0x20202020U, dst.B);
    }

    [Fact]
    public static void CopyFromLocalField()
    {
        SWithInner src;
        src.S = s_static;
        S dst;
        dst = src.S;
        dst.A = dst.B + 3;
        dst.B = 0x20202020;
        Consume(dst);
        Assert.Equal(0x20202023U, dst.A);
        Assert.Equal(0x20202020U, dst.B);
    }

    [Fact]
    public static void CopyFromBlk()
    {
        S dst;
        dst = s_static;
        dst.A = dst.B + 3;
        dst.B = 0x20202020;
        Consume(dst);
        Assert.Equal(0x20202023U, dst.A);
        Assert.Equal(0x20202020U, dst.B);
    }

    [Fact]
    public static void CopyToBlk()
    {
        S s = default;
        CopyToBlkInner(ref s);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CopyToBlkInner(ref S mutate)
    {
        S src = s_static;
        src.A = src.B + 3;
        src.B = 0x20202020;
        mutate = src;
        Assert.Equal(0x20202023U, mutate.A);
        Assert.Equal(0x20202020U, mutate.B);
    }

    private static VeryOverlapping _overlappy1 = new VeryOverlapping { F0 = 0x12345678, F4 = 0xdeadbeef };
    private static VeryOverlapping _overlappy2 = new VeryOverlapping { F1 = 0xde, F2 = 0x1357, F5 = 0x17, F7 = 0x42 };

    [Fact]
    public static void Overlappy()
    {
        VeryOverlapping lcl1 = _overlappy1;
        VeryOverlapping lcl2 = _overlappy2;
        VeryOverlapping lcl3 = _overlappy1;

        lcl1.F0 = lcl3.F0 + 3;
        lcl1.F4 = lcl3.F0 + lcl3.F4;

        lcl3 = lcl1;

        lcl2.F1 = (byte)(lcl2.F2 + lcl2.F5 + lcl2.F7);
        lcl1 = lcl2;

        Consume(lcl1);
        Consume(lcl2);
        Consume(lcl3);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(-17)]
    public static void StoreCallResult(int value)
    {
        ReturnPair pair = MakePair(value, value + 11);
        Assert.Equal(value, pair.A);
        Assert.Equal(value + 11, pair.B);

        pair = MakePair(pair.B, pair.A);
        Assert.Equal(value + 11, pair.A);
        Assert.Equal(value, pair.B);
        Consume(pair);

        MixedReturn mixed = MakeMixed(value, value + 0.5);
        Assert.Equal(value, mixed.A);
        Assert.Equal(value + 0.5, mixed.B);
        Consume(mixed);

        FloatingReturn floating = MakeFloating(value);
        Assert.Equal(value + 0.25, floating.A);
        Assert.Equal(value + 0.5, floating.B);
        Assert.Equal(value + 0.75, floating.C);
        Assert.Equal(value + 1.0, floating.D);
        Consume(floating);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(-17)]
    public static void StorePackedCallResult(int value)
    {
        PackedRegisterReturn registers = MakePackedRegisters(value);
        Assert.Equal(value, registers.A);
        Assert.Equal(value + 1, registers.B);
        Assert.Equal(value + 2, registers.C);
        Assert.Equal(value + 3, registers.D);
        Consume(registers);

        PackedReturn packed = MakePacked(value);
        Assert.Equal((byte)value, packed.A);
        Assert.Equal((short)(value + 1), packed.B);
        Assert.Equal(value + 2, packed.C);
        Assert.Equal(value + 3, packed.D);
        Assert.Equal((byte)(value + 4), packed.E);
        Consume(packed);

        ReturnContainer container = default;
        container.Before = value + 20;
        container.Pair = MakePair(value, value + 11);
        container.After = value + 30;
        Consume(container);
        container.Pair = MakePair(container.Pair.B, container.Pair.A);
        Assert.Equal(value + 20, container.Before);
        Assert.Equal(value + 11, container.Pair.A);
        Assert.Equal(value, container.Pair.B);
        Assert.Equal(value + 30, container.After);
        Consume(container);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(-17)]
    public static void StoreCallResultInLoop(int value)
    {
        ReturnPair pair = MakePair(value, value + 1);
        long expectedA = value;
        long expectedB = value + 1;
        for (int i = 0; i < 20; i++)
        {
            if ((i & 1) == 0)
            {
                pair = MakePair(pair.B, pair.A + i);
                (expectedA, expectedB) = (expectedB, expectedA + i);
            }
            else
            {
                pair = MakePair(pair.A - i, pair.B);
                expectedA -= i;
            }

            Assert.Equal(expectedA, pair.A);
            Assert.Equal(expectedB, pair.B);
        }

        Consume(pair);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(-17)]
    public static void StoreCallResultWithDeadFields(int value)
    {
        int calls = 0;
        ReturnPair pair = MakeCountedPair(value, ref calls);
        pair.A = value + 10;
        Assert.Equal(value + 10, pair.A);
        Assert.Equal(value + 1, pair.B);
        pair = MakeCountedPair(value + 20, ref calls);
        pair.B = value + 30;
        Assert.Equal(value + 20, pair.A);
        Assert.Equal(value + 30, pair.B);
        pair = MakeCountedPair(value + 40, ref calls);
        Assert.Equal(3, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static void StoreThrowingCallResult(bool shouldThrow)
    {
        ReturnPair pair = MakePair(31, 47);
        try
        {
            pair = MakeThrowingPair(shouldThrow);
        }
        catch (InvalidOperationException) when (shouldThrow)
        {
        }

        Assert.Equal(shouldThrow ? 31 : 71, pair.A);
        Assert.Equal(shouldThrow ? 47 : 89, pair.B);
        Consume(pair);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(-17)]
    public static void StoreCallResultWithReferences(int value)
    {
        object expected = new object();
        ReferenceReturn result = MakeReference(expected, value);
        Collect();
        Assert.Same(expected, result.A);
        Assert.Equal(value, result.B);
        Consume(result);

        ByrefReturn byrefResult = MakeByref(ref value);
        Collect();
        byrefResult.A += 5;
        Assert.Equal(value, byrefResult.A);
        Assert.Equal(value - 5, byrefResult.B);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(32768)]
    public static void StoreSmallCallResult(int value)
    {
        SignedByteReturn signedByte = MakeSignedByte(value);
        SignedShortReturn signedShort = MakeSignedShort(value);
        UnsignedByteReturn unsignedByte = MakeUnsignedByte(value);
        UnsignedShortReturn unsignedShort = MakeUnsignedShort(value);
        Collect();
        Assert.Equal((int)(sbyte)value, (int)signedByte.Value);
        Assert.Equal((int)(short)value, (int)signedShort.Value);
        Assert.Equal((int)(byte)value, (int)unsignedByte.Value);
        Assert.Equal((int)(ushort)value, (int)unsignedShort.Value);
        Consume(signedByte);
        Consume(signedShort);
        Consume(unsignedByte);
        Consume(unsignedShort);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static void StoreCallResultAcrossHandlers(bool shouldThrow)
    {
        object expected = new object();
        ReferenceReturn result = MakeReference(new object(), 31);
        try
        {
            result = MakeReference(expected, 47);
            if (shouldThrow)
            {
                throw new InvalidOperationException();
            }
        }
        catch (InvalidOperationException) when (shouldThrow)
        {
            Collect();
            Assert.Same(expected, result.A);
            Assert.Equal(47, result.B);
        }
        finally
        {
            Collect();
            Assert.Same(expected, result.A);
            Assert.Equal(47, result.B);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ReturnPair MakePair(long a, long b) => new ReturnPair { A = a, B = b };

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static MixedReturn MakeMixed(long a, double b) => new MixedReturn { A = a, B = b };

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static FloatingReturn MakeFloating(int value) =>
        new FloatingReturn { A = value + 0.25, B = value + 0.5, C = value + 0.75, D = value + 1.0 };

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static PackedRegisterReturn MakePackedRegisters(int value) =>
        new PackedRegisterReturn { A = value, B = value + 1, C = value + 2, D = value + 3 };

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static PackedReturn MakePacked(int value) =>
        new PackedReturn { A = (byte)value, B = (short)(value + 1), C = value + 2, D = value + 3, E = (byte)(value + 4) };

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ReturnPair MakeCountedPair(int value, ref int calls)
    {
        calls++;
        return new ReturnPair { A = value, B = value + 1 };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ReturnPair MakeThrowingPair(bool shouldThrow)
    {
        if (shouldThrow)
        {
            throw new InvalidOperationException();
        }

        return new ReturnPair { A = 71, B = 89 };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ReferenceReturn MakeReference(object value, long number) =>
        new ReferenceReturn { A = value, B = number };

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ByrefReturn MakeByref(ref int value) => new ByrefReturn { A = ref value, B = value };

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static SignedByteReturn MakeSignedByte(int value) => new SignedByteReturn { Value = (sbyte)value };

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static SignedShortReturn MakeSignedShort(int value) => new SignedShortReturn { Value = (short)value };

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static UnsignedByteReturn MakeUnsignedByte(int value) => new UnsignedByteReturn { Value = (byte)value };

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static UnsignedShortReturn MakeUnsignedShort(int value) => new UnsignedShortReturn { Value = (ushort)value };

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Collect() => GC.Collect();

    private struct ReturnPair
    {
        public long A, B;
    }

    private struct MixedReturn
    {
        public long A;
        public double B;
    }

    private struct FloatingReturn
    {
        public double A, B, C, D;
    }

    private struct PackedRegisterReturn
    {
        public int A, B, C, D;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct PackedReturn
    {
        public byte A;
        public short B;
        public int C;
        public long D;
        public byte E;
    }

    private struct ReturnContainer
    {
        public long Before;
        public ReturnPair Pair;
        public long After;
    }

    private struct ReferenceReturn
    {
        public object A;
        public long B;
    }

    private ref struct ByrefReturn
    {
        public ref int A;
        public long B;
    }

    private struct SignedByteReturn
    {
        public sbyte Value;
    }

    private struct SignedShortReturn
    {
        public short Value;
    }

    private struct UnsignedByteReturn
    {
        public byte Value;
    }

    private struct UnsignedShortReturn
    {
        public ushort Value;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Consume<T>(T val)
    {
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct S
    {
        [FieldOffset(0)]
        public uint A;
        [FieldOffset(4)]
        public uint B;
        [FieldOffset(2)]
        public uint C;
    }

    private struct SWithInner
    {
        public int Field;
        public S S;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct VeryOverlapping
    {
        [FieldOffset(0)]
        public uint F0;
        [FieldOffset(1)]
        public byte F1;
        [FieldOffset(2)]
        public ushort F2;
        [FieldOffset(3)]
        public byte F3;
        [FieldOffset(4)]
        public uint F4;
        [FieldOffset(5)]
        public byte F5;
        [FieldOffset(6)]
        public ushort F6;
        [FieldOffset(7)]
        public byte F7;
    }
}
