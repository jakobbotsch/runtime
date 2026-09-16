// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Runtime.CompilerServices;
using Xunit;

namespace TestCompareExtend
{
    public class Program
    {
        static int result = 100;
        static int failCtr = 0;

        [Theory]
        [InlineData(-65537L)]
        [InlineData(-32769L)]
        [InlineData(-32768L)]
        [InlineData(-129L)]
        [InlineData(-128L)]
        [InlineData(-1L)]
        [InlineData(0L)]
        [InlineData(1L)]
        [InlineData(127L)]
        [InlineData(128L)]
        [InlineData(255L)]
        [InlineData(256L)]
        [InlineData(32767L)]
        [InlineData(32768L)]
        [InlineData(65535L)]
        [InlineData(65536L)]
        [InlineData(long.MinValue)]
        [InlineData(long.MaxValue)]
        public static void CheckMemoryCompareRanges(long input)
        {
            long[] values = { -65537, -32769, -32768, -129, -128, -1, 0, 1, 127, 128, 255, 256,
                              32767, 32768, 65535, 65536, long.MinValue, long.MaxValue };
            long packedByte = unchecked(input << 8) | 0xA5;
            long packedHighByte = unchecked(input << 40) | 0xA55A;
            long packedShort = unchecked(input << 16) | 0xA55A;
            foreach (long value in values)
            {
                byte unsignedByte = (byte)value;
                sbyte signedByte = (sbyte)value;
                ushort unsignedShort = (ushort)value;
                short signedShort = (short)value;

                Assert.Equal(unsignedByte == (byte)(packedHighByte >> 40), MemoryByteEquals(ref unsignedByte, packedHighByte));
                Assert.Equal(signedByte < (sbyte)(packedByte >> 8), MemorySByteLessThan(ref signedByte, packedByte));
                Assert.Equal(unsignedShort >= (ushort)(packedShort >> 16), MemoryUShortGreaterOrEqual(ref unsignedShort, packedShort));
                Assert.Equal((short)(packedShort >> 16) > signedShort, MemoryShortLessThan(ref signedShort, packedShort));
                Assert.Equal((uint)signedByte < (uint)(sbyte)input, MemorySByteUnsignedLessThan(ref signedByte, input));
                Assert.Equal(unsignedByte == (input < value ? 1 : 0), MemoryByteEqualsBoolean(ref unsignedByte, input, value));
                Assert.Equal(unsignedByte == (sbyte)input, MemoryByteEqualsSigned(ref unsignedByte, input));
                Assert.Equal(signedByte == (byte)input, MemorySByteEqualsUnsigned(ref signedByte, input));
                Assert.Equal(unsignedShort == (short)input, MemoryUShortEqualsSigned(ref unsignedShort, input));
                Assert.Equal(signedShort == (ushort)input, MemoryShortEqualsUnsigned(ref signedShort, input));
            }

            byte checkedValue = (byte)input;
            if (input is >= byte.MinValue and <= byte.MaxValue)
            {
                Assert.True(MemoryByteEqualsChecked(ref checkedValue, input));
            }
            else
            {
                Assert.Throws<OverflowException>(() => MemoryByteEqualsChecked(ref checkedValue, input));
            }

            byte originalValue = checkedValue;
            Assert.True(MemoryByteEqualsWithMutation(ref checkedValue));
            Assert.Equal(unchecked((byte)(originalValue + 1)), checkedValue);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool MemoryByteEquals(ref byte value, long input)
        {
            // X64: cmp byte ptr [{{.*}}], {{.*}}
            return value == (byte)(input >> 40);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool MemorySByteLessThan(ref sbyte value, long input)
        {
            // X64: cmp byte ptr [{{.*}}], {{.*}}
            return value < (sbyte)(input >> 8);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool MemoryUShortGreaterOrEqual(ref ushort value, long input)
        {
            // X64: cmp word ptr [{{.*}}], {{.*}}
            return value >= (ushort)(input >> 16);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool MemoryShortLessThan(ref short value, long input)
        {
            // X64: cmp {{.*}}, word ptr [{{.*}}]
            return (short)(input >> 16) > value;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool MemorySByteUnsignedLessThan(ref sbyte value, long input)
        {
            return (uint)value < (uint)(sbyte)input;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool MemoryByteEqualsBoolean(ref byte value, long left, long right)
        {
            // X64: cmp {{.*}}, byte ptr [{{.*}}]
            return value == (left < right ? 1 : 0);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool MemoryByteEqualsChecked(ref byte value, long input)
        {
            return value == checked((byte)input);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool MemoryByteEqualsSigned(ref byte value, long input)
        {
            return value == (sbyte)input;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool MemorySByteEqualsUnsigned(ref sbyte value, long input)
        {
            return value == (byte)input;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool MemoryUShortEqualsSigned(ref ushort value, long input)
        {
            return value == (short)input;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool MemoryShortEqualsUnsigned(ref short value, long input)
        {
            return value == (ushort)input;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool MemoryByteEqualsWithMutation(ref byte value)
        {
            return value == ReadAndIncrement(ref value);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static byte ReadAndIncrement(ref byte value)
        {
            byte original = value;
            value++;
            return original;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [Fact]
        public static int CheckCompareExtend()
        {
            // Signed / Unsigned
            AssertTrue(SByteByte(12, 12));
            AssertFalse(SByteByte(-12, 34));

            AssertTrue(ShortByte(12, 12));
            AssertFalse(ShortByte(-12, 12));

            AssertTrue(ShortUShort(12, 12));
            AssertFalse(ShortUShort(-12, 12));

            AssertTrue(IntByte(12, 12));
            AssertFalse(IntByte(-12, 34));

            AssertTrue(IntUShort(12, 12));
            AssertFalse(IntUShort(-12, 34));

            AssertTrue(IntUInt(12, 12));
            AssertFalse(IntUInt(-12, 34));

            AssertTrue(LongByte(12, 12));
            AssertFalse(LongByte(-12, 34));

            AssertTrue(LongUShort(12, 12));
            AssertFalse(LongUShort(-12, 34));

            AssertTrue(LongUInt(12, 12));
            AssertFalse(LongUInt(-12, 34));

            // Signed / Signed
            AssertTrue(SByteSByte(12, 12));
            AssertFalse(SByteSByte(12, 34));

            AssertTrue(ShortSByte(12, 12));
            AssertFalse(ShortSByte(-1234, -12));

            AssertTrue(ShortShort(1234, 1234));
            AssertFalse(ShortShort(1234, 3456));

            AssertTrue(IntSByte(12, 12));
            AssertFalse(IntSByte(-12, -34));

            AssertTrue(IntShort(1234, 1234));
            AssertFalse(IntShort(1234, -1234));

            AssertTrue(LongSByte(12, 12));
            AssertFalse(LongSByte(12, -34));

            AssertTrue(LongShort(12, 12));
            AssertFalse(LongShort(-12, 34));

            AssertTrue(LongInt(12, 12));
            AssertFalse(LongInt(12, -34));

            // Unsigned / Signed
            AssertTrue(ByteSByte(12, 12));
            AssertFalse(ByteSByte(12, -12));

            AssertTrue(UShortSByte(12, 12));
            AssertFalse(UShortSByte(12, -12));

            AssertTrue(UShortShort(1234, 1234));
            AssertFalse(UShortShort(1234, -1234));

            AssertTrue(UIntSByte(12, 12));
            AssertFalse(UIntSByte(12, -12));

            AssertTrue(UIntShort(1234, 1234));
            AssertFalse(UIntShort(1234, -1234));

            AssertTrue(UIntInt(1234, 1234));
            AssertFalse(UIntInt(1234, -1234));

            AssertTrue(ULongSByte(12, 12));
            AssertFalse(ULongSByte(12, -12));

            AssertTrue(ULongShort(1234, 1234));
            AssertFalse(ULongShort(1234, -1234));

            AssertTrue(ULongInt(1234, 1234));
            AssertFalse(ULongInt(1234, -1234));

            // Unsigned / Unsigned
            AssertTrue(ByteByte(12, 12));
            AssertFalse(ByteByte(12, 34));

            AssertTrue(UShortByte(12, 12));
            AssertFalse(UShortByte(12, 34));

            AssertTrue(UShortUShort(1234, 1234));
            AssertFalse(UShortUShort(1234, 3456));

            AssertTrue(UIntByte(12, 12));
            AssertFalse(UIntByte(12, 34));

            AssertTrue(UIntUShort(1234, 1234));
            AssertFalse(UIntUShort(1234, 3456));

            AssertTrue(ULongByte(12, 12));
            AssertFalse(ULongByte(12, 34));

            AssertTrue(ULongShort(1234, 1234));
            AssertFalse(ULongShort(1234, -3456));

            AssertTrue(ULongUShort(1234, 1234));
            AssertFalse(ULongUShort(1234, 3456));

            AssertTrue(ULongUInt(1234, 1234));
            AssertFalse(ULongUInt(1234, 3456));

            return result + failCtr;
        }

        static void AssertTrue(bool b)
        {
            if (!b)
            {
                failCtr += 1;
            }
        }

        static void AssertFalse(bool b)
        {
            AssertTrue(!b);
        }


        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool ByteByte(long a, long b)
        {
            //ARM64-FULL-LINE: cmp {{w[0-9]+}}, {{w[0-9]+}}, UXTB
            return (byte)a == (byte)b;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool ByteSByte(long a, long b)
        {
            //ARM64-FULL-LINE: cmp {{w[0-9]+}}, {{w[0-9]+}}, SXTB
            return (byte)a == (sbyte)b;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool SByteSByte(long a, long b)
        {
            //ARM64-FULL-LINE: cmp {{w[0-9]+}}, {{w[0-9]+}}, SXTB
            return (sbyte)a == (sbyte)b;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool SByteByte(long a, long b)
        {
            //ARM64-FULL-LINE: cmp {{w[0-9]+}}, {{w[0-9]+}}, UXTB
            return (sbyte)a == (byte)b;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool ShortByte(long a, long b)
        {
            //ARM64-FULL-LINE: cmp {{w[0-9]+}}, {{w[0-9]+}}, UXTB
            return (short)a == (byte)b;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool ShortSByte(long a, long b)
        {
            //ARM64-FULL-LINE: cmp {{w[0-9]+}}, {{w[0-9]+}}, SXTB
            return (short)a == (sbyte)b;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool ShortShort(long a, long b)
        {
            //ARM64-FULL-LINE: cmp {{w[0-9]+}}, {{w[0-9]+}}, SXTH
            return (short)a == (short)b;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool ShortUShort(long a, long b)
        {
            //ARM64-FULL-LINE: cmp {{w[0-9]+}}, {{w[0-9]+}}, UXTH
            return (short)a == (ushort)b;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool UShortByte(long a, long b)
        {
            //ARM64-FULL-LINE: cmp {{w[0-9]+}}, {{w[0-9]+}}, UXTB
            return (ushort)a == (byte)b;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool UShortSByte(long a, long b)
        {
            //ARM64-FULL-LINE: cmp {{w[0-9]+}}, {{w[0-9]+}}, SXTB
            return (ushort)a == (sbyte)b;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool UShortShort(long a, long b)
        {
            //ARM64-FULL-LINE: cmp {{w[0-9]+}}, {{w[0-9]+}}, SXTH
            return (ushort)a == (short)b;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool UShortUShort(long a, long b)
        {
            //ARM64-FULL-LINE: cmp {{w[0-9]+}}, {{w[0-9]+}}, UXTH
            return (ushort)a == (ushort)b;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool IntByte(long a, long b)
        {
            //ARM64-FULL-LINE: cmp {{w[0-9]+}}, {{w[0-9]+}}, UXTB
            return (int)a == (byte)b;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool IntSByte(long a, long b)
        {
            //ARM64-FULL-LINE: cmp {{w[0-9]+}}, {{w[0-9]+}}, SXTB
            return (int)a == (sbyte)b;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool IntShort(long a, long b)
        {
            //ARM64-FULL-LINE: cmp {{w[0-9]+}}, {{w[0-9]+}}, SXTH
            return (int)a == (short)b;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool IntUShort(long a, long b)
        {
            //ARM64-FULL-LINE: cmp {{w[0-9]+}}, {{w[0-9]+}}, UXTH
            return (int)a == (ushort)b;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool IntUInt(long a, long b)
        {
            //ARM64-FULL-LINE: cmp {{x[0-9]+}}, {{w[0-9]+}}, UXTW
            return (int)a == (uint)b;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool UIntByte(long a, long b)
        {
            //ARM64-FULL-LINE: cmp {{w[0-9]+}}, {{w[0-9]+}}, UXTB
            return (uint)a == (byte)b;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool UIntUShort(long a, long b)
        {
            //ARM64-FULL-LINE: cmp {{w[0-9]+}}, {{w[0-9]+}}, UXTH
            return (uint)a == (ushort)b;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool UIntSByte(long a, long b)
        {
            //ARM64-FULL-LINE: cmp {{x[0-9]+}}, {{w[0-9]+}}, SXTW
            return (uint)a == (sbyte)b;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool UIntShort(long a, long b)
        {
            //ARM64-FULL-LINE: cmp {{x[0-9]+}}, {{w[0-9]+}}, SXTW
            return (uint)a == (short)b;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool UIntInt(long a, long b)
        {
            //ARM64-FULL-LINE: cmp {{x[0-9]+}}, {{w[0-9]+}}, SXTW
            return (uint)a == (int)b;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool LongByte(long a, long b)
        {
            //ARM64-FULL-LINE: cmp {{x[0-9]+}}, {{w[0-9]+}}, UXTW
            return a == (byte)b;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool LongSByte(long a, long b)
        {
            //ARM64-FULL-LINE: cmp {{x[0-9]+}}, {{w[0-9]+}}, SXTW
            return a == (sbyte)b;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool LongShort(long a, long b)
        {
            //ARM64-FULL-LINE: cmp {{x[0-9]+}}, {{w[0-9]+}}, SXTW
            return a == (short)b;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool LongUShort(long a, long b)
        {
            //ARM64-FULL-LINE: cmp {{x[0-9]+}}, {{w[0-9]+}}, UXTW
            return a == (ushort)b;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool LongInt(long a, long b)
        {
            //ARM64-FULL-LINE: cmp {{x[0-9]+}}, {{w[0-9]+}}, SXTW
            return a == (int)b;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool LongUInt(long a, long b)
        {
            //ARM64-FULL-LINE: cmp {{x[0-9]+}}, {{w[0-9]+}}, UXTW
            return a == (uint)b;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool ULongByte(long a, long b)
        {
            //ARM64-FULL-LINE: cmp {{x[0-9]+}}, {{w[0-9]+}}, UXTW
            return (ulong)a == (byte)b;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool ULongSByte(long a, long b)
        {
            //ARM64-FULL-LINE: cmp {{x[0-9]+}}, {{w[0-9]+}}, SXTW
            return (ulong)a == (ulong)(sbyte)b;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool ULongShort(long a, long b)
        {
            //ARM64-FULL-LINE: cmp {{x[0-9]+}}, {{w[0-9]+}}, SXTW
            return (ulong)a == (ulong)(short)b;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool ULongUShort(long a, long b)
        {
            //ARM64-FULL-LINE: cmp {{x[0-9]+}}, {{w[0-9]+}}, UXTW
            return (ulong)a == (ushort)b;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool ULongInt(long a, long b)
        {
            //ARM64-FULL-LINE: cmp {{x[0-9]+}}, {{w[0-9]+}}, SXTW
            return (ulong)a == (ulong)(int)b;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool ULongUInt(long a, long b)
        {
            //ARM64-FULL-LINE: cmp {{x[0-9]+}}, {{w[0-9]+}}, UXTW
            return (ulong)a == (uint)b;
        }
    }
}