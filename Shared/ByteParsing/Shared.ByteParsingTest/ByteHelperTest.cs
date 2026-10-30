using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Shared.ByteParsing;

namespace Shared.ByteParsingTest
{
    [TestFixture]
    public class ByteHelperTest
    {
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct ByValArrayLayout
        {
            [MarshalAs(
                UnmanagedType.ByValArray,
                SizeConst = 3,
                ArraySubType = UnmanagedType.I2)]
            public short[] Values;

            [MarshalAs(UnmanagedType.Bool)]
            public bool Enabled;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct PackedPrimitiveLayout
        {
            public byte Prefix;
            public int Value;
        }

        [Test]
        public void GetSize_PrimitivesUseMarshaledSizes()
        {
            Assert.That(ByteHelper.GetSize<byte>(), Is.EqualTo(1));
            Assert.That(ByteHelper.GetSize<bool>(), Is.EqualTo(4));
        }

        [Test]
        public void GetSize_ByValArrayUsesMarshaledLayoutNotManagedSize()
        {
            var sample = new ByValArrayLayout
            {
                Values = [1, 2, 3],
                Enabled = true,
            };
            var marshaledSize = Marshal.SizeOf(sample);
            var managedSize = Unsafe.SizeOf<ByValArrayLayout>();

            Assert.That(marshaledSize, Is.EqualTo(10));
            Assert.That(managedSize, Is.Not.EqualTo(marshaledSize));
            Assert.That(
                ByteHelper.GetSize<ByValArrayLayout>(),
                Is.EqualTo(marshaledSize));
            Assert.That(
                ByteHelper.GetSize<ByValArrayLayout>(),
                Is.EqualTo(marshaledSize));
        }

        [Test]
        public void GetSize_UsesPackedPrimitiveLayout()
        {
            _ = new PackedPrimitiveLayout
            {
                Prefix = 0x5a,
                Value = 0x12345678,
            };

            Assert.That(ByteHelper.GetSize<PackedPrimitiveLayout>(), Is.EqualTo(5));
        }

        [Test]
        public void GetSize_ConcurrentColdAccessReturnsStableSize()
        {
            const int accessCount = 64;
            var sizes = new int[accessCount];

            Parallel.For(
                0,
                accessCount,
                index => sizes[index] = ByteHelper.GetSize<PackedPrimitiveLayout>());

            Assert.That(sizes, Is.All.EqualTo(5));
        }

        [Test]
        public void GetSize_InvalidMarshaledTypePreservesArgumentException()
        {
            Assert.Throws<ArgumentException>(() => ByteHelper.GetSize<string>());
        }
    }
}