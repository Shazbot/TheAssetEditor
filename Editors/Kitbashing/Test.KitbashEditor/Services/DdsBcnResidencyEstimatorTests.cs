using System.Buffers.Binary;
using Editors.KitbasherEditor.Services;

namespace Test.KitbashEditor.Services
{
    public class DdsBcnResidencyEstimatorTests
    {
        [Test]
        public void TryEstimate_LegacyDxt1_SumsDeclaredMipChain()
        {
            var header = CreateLegacyHeader(
                width: 8,
                height: 8,
                mipCount: 4,
                fourCc: 0x31545844); // DXT1

            var success = DdsBcnResidencyEstimator.TryEstimate(header, out var estimate);

            Assert.Multiple(() =>
            {
                Assert.That(success, Is.True);
                Assert.That(estimate.Format, Is.EqualTo("BC1"));
                Assert.That(estimate.MipCount, Is.EqualTo(4));
                Assert.That(estimate.Bytes, Is.EqualTo(56));
            });
        }

        [Test]
        public void TryEstimate_Dx10Bc7_SumsDeclaredMipChain()
        {
            var header = CreateDx10Header(
                width: 16,
                height: 8,
                mipCount: 5,
                dxgiFormat: 98); // BC7_UNORM

            var success = DdsBcnResidencyEstimator.TryEstimate(header, out var estimate);

            Assert.Multiple(() =>
            {
                Assert.That(success, Is.True);
                Assert.That(estimate.Format, Is.EqualTo("BC7"));
                Assert.That(estimate.MipCount, Is.EqualTo(5));
                Assert.That(estimate.Bytes, Is.EqualTo(208));
            });
        }

        [Test]
        public void TryEstimate_RejectsNonBcnAndDx10Arrays()
        {
            var uncompressed = CreateLegacyHeader(
                width: 4,
                height: 4,
                mipCount: 1,
                fourCc: 0);
            var array = CreateDx10Header(
                width: 4,
                height: 4,
                mipCount: 1,
                dxgiFormat: 71,
                arraySize: 2);

            Assert.Multiple(() =>
            {
                Assert.That(DdsBcnResidencyEstimator.TryEstimate(uncompressed, out _), Is.False);
                Assert.That(DdsBcnResidencyEstimator.TryEstimate(array, out _), Is.False);
            });
        }

        private static byte[] CreateLegacyHeader(
            int width,
            int height,
            int mipCount,
            uint fourCc)
        {
            var header = new byte[128];
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), 0x20534444);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), 124);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12, 4), height);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16, 4), width);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28, 4), mipCount);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(84, 4), fourCc);
            return header;
        }

        private static byte[] CreateDx10Header(
            int width,
            int height,
            int mipCount,
            uint dxgiFormat,
            uint arraySize = 1)
        {
            var header = new byte[148];
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), 0x20534444);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), 124);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12, 4), height);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16, 4), width);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28, 4), mipCount);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(84, 4), 0x30315844);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(128, 4), dxgiFormat);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(140, 4), arraySize);
            return header;
        }
    }
}
