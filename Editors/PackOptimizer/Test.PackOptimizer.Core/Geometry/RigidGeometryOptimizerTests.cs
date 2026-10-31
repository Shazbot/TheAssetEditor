using Editors.PackOptimizer.Core;
using GameWorld.MeshOptimizer;
using System.Buffers.Binary;
using Shared.ByteParsing;
using System.Numerics;
using Shared.GameFormats.RigidModel;
using Shared.GameFormats.RigidModel.LodHeader;
using Shared.GameFormats.RigidModel.MaterialHeaders;
using Shared.GameFormats.RigidModel.Vertex;

namespace Test.PackOptimizer.Core
{
    public class RigidGeometryOptimizerTests
    {
        [Test]
        public void Optimize_FiltersDegenerateAndDuplicateTrianglesWhileRemovingUnusedAndDuplicateVertices()
        {
            var model = CreateModel(
                [
                    Vertex(0, 0, 0),
                    Vertex(1, 0, 0),
                    Vertex(0, 1, 0),
                    Vertex(0, 1, 0),
                    Vertex(5, 5, 5),
                ],
                [
                    0, 1, 2,
                    0, 1, 3,
                    0, 0, 1,
                ]);

            var statistics = RigidGeometryOptimizer.Optimize(model);

            Assert.Multiple(() =>
            {
                Assert.That(statistics.VerticesBefore, Is.EqualTo(5));
                Assert.That(statistics.VerticesAfter, Is.EqualTo(3));
                Assert.That(statistics.UnreferencedVerticesRemoved, Is.EqualTo(1));
                Assert.That(statistics.DuplicateVerticesRemoved, Is.EqualTo(1));
                Assert.That(statistics.IndicesBefore, Is.EqualTo(9));
                Assert.That(statistics.IndicesAfter, Is.EqualTo(3));
                Assert.That(model.Mesh.VertexList, Has.Length.EqualTo(3));
                Assert.That(model.Mesh.IndexList, Has.Length.EqualTo(3));
            });
        }

        [Test]
        public void Optimize_FiltersTriangleMadeDegenerateByFullPayloadVertexDeduplication()
        {
            var model = CreateModel(
                [
                    Vertex(0, 0, 0),
                    Vertex(1, 0, 0),
                    Vertex(0, 1, 0),
                    Vertex(0, 1, 0),
                ],
                [
                    0, 1, 2,
                    0, 2, 3,
                ]);

            var triangleCountBefore = model.Mesh.IndexList.Length / 3;

            var statistics = RigidGeometryOptimizer.Optimize(model);

            Assert.Multiple(() =>
            {
                Assert.That(statistics.DuplicateVerticesRemoved, Is.EqualTo(1));
                Assert.That(model.Mesh.IndexList.Length / 3, Is.EqualTo(triangleCountBefore - 1));
                Assert.That(statistics.IndicesAfter, Is.EqualTo(3));
            });
        }

        [Test]
        public void Optimize_PreservesNonDegenerateTriangleGeometry()
        {
            var model = CreateModel(
                [
                    Vertex(0, 0, 0),
                    Vertex(1, 0, 0),
                    Vertex(0, 1, 0),
                    Vertex(1, 1, 0),
                ],
                [
                    0, 1, 2,
                    2, 1, 3,
                ]);

            var before = TrianglePositionKeys(model);

            var statistics = RigidGeometryOptimizer.Optimize(model);

            Assert.Multiple(() =>
            {
                Assert.That(model.Mesh.IndexList, Has.Length.EqualTo(6));
                Assert.That(TrianglePositionKeys(model), Is.EquivalentTo(before));
            });
        }

        [Test]
        public void Optimize_PreservesOrientedTrianglesAndFullVertexPayloadsWithNativeOrdering()
        {
            var vertices = new[]
            {
                Vertex(0, 0, 8, 1),
                Vertex(1, 0, 8, 2),
                Vertex(0, 1, 8, 3),
                Vertex(0, 0, 0, 4),
                Vertex(1, 0, 0, 5),
                Vertex(0, 1, 0, 6),
                Vertex(0, 0, 4, 7),
                Vertex(1, 0, 4, 8),
                Vertex(0, 1, 4, 9),
                Vertex(0, 0, 12, 10),
            };
            ushort[] indices =
            [
                6, 7, 8,
                0, 1, 2,
                3, 4, 5,
                9, 9, 0,
            ];
            var model = CreateModel(vertices, (ushort[])indices.Clone());
            var before = TrianglePayloadKeys(vertices, indices).Take(3).ToArray();

            var statistics = RigidGeometryOptimizer.Optimize(model);

            Assert.Multiple(() =>
            {
                Assert.That(statistics.IndicesAfter, Is.EqualTo(indices.Length - 3));
                Assert.That(TrianglePayloadKeys(model), Is.EquivalentTo(before));
            });
        }

        [Test]
        public void Optimize_FiltersCyclicAndExactDuplicatesButKeepsReverseWinding()
        {
            var a = Vertex(0, 0, 0);
            var b = Vertex(1, 0, 0);
            var c = Vertex(0, 1, 0);
            var vertices = new[] { a, b, c, Clone(a), Clone(b), Clone(c) };
            ushort[] indices = [0, 1, 2, 1, 2, 0, 3, 4, 5, 0, 2, 1];
            var model = CreateModel(vertices, (ushort[])indices.Clone());
            var expected = TrianglePayloadKeys(vertices, [0, 1, 2, 0, 2, 1]);

            var statistics = RigidGeometryOptimizer.Optimize(model);

            Assert.Multiple(() =>
            {
                Assert.That(statistics.DuplicateVerticesRemoved, Is.EqualTo(3));
                Assert.That(statistics.IndicesBefore, Is.EqualTo(12));
                Assert.That(statistics.IndicesAfter, Is.EqualTo(6));
                Assert.That(TrianglePayloadKeys(model), Is.EquivalentTo(expected));
            });
        }

        [Test]
        public void Optimize_FullPayloadKeyPreservesDistinctUvNormalTangentBoneAndColourValues()
        {
            var baseVertex = Vertex(0, 0, 0);
            var vertices = new[]
            {
                baseVertex,
                Clone(baseVertex),
                Clone(baseVertex),
                Clone(baseVertex),
                Clone(baseVertex),
                Clone(baseVertex),
                Clone(baseVertex),
                Clone(baseVertex),
                Clone(baseVertex),
                Clone(baseVertex),
            };
            vertices[2].Uv = new Vector2(0.25f, 0.75f);
            vertices[3].Uv1 = new Vector2(0.25f, 0.75f);
            vertices[4].Normal = new Vector3(0, 1, 0);
            vertices[5].BiNormal = new Vector3(1, 0, 0);
            vertices[6].Tangent = new Vector3(0, -1, 0);
            vertices[7].BoneIndex = [1];
            vertices[8].BoneWeight = [0.5f];
            vertices[9].Colour = new Vector4(0.5f, 1, 1, 1);
            ushort[] indices = [0, 1, 2, 0, 3, 4, 0, 5, 6, 0, 7, 8, 9, 3, 4];
            var model = CreateModel(vertices, indices);
            var expected = TrianglePayloadKeys(vertices, [0, 3, 4, 0, 5, 6, 0, 7, 8, 9, 3, 4]);

            var statistics = RigidGeometryOptimizer.Optimize(model);

            Assert.Multiple(() =>
            {
                Assert.That(statistics.IndicesAfter, Is.EqualTo(12));
                Assert.That(TrianglePayloadKeys(model), Is.EquivalentTo(expected));
                Assert.That(model.Mesh.VertexList, Has.Length.EqualTo(8));
            });
        }

        [Test]
        public void Optimize_CanonicalizesSignedZeroAndNanButDistinguishesNullAndEmptyBoneArrays()
        {
            var first = Vertex(0, 0, 0);
            first.Normal = new Vector3(float.NaN, 0, 1);
            var samePayload = Clone(first);
            samePayload.Position = new Vector4(-0f, 0, 0, 1);
            samePayload.Normal = new Vector3(BitConverter.Int32BitsToSingle(unchecked((int)0x7fc12345)), 0, 1);
            var second = Vertex(1, 0, 0);
            var third = Vertex(0, 1, 0);
            var vertices = new[] { first, second, third, samePayload };
            ushort[] indices = [0, 1, 2, 3, 2, 1];
            var model = CreateModel(vertices, (ushort[])indices.Clone());

            var statistics = RigidGeometryOptimizer.Optimize(model);

            Assert.Multiple(() =>
            {
                Assert.That(statistics.DuplicateVerticesRemoved, Is.EqualTo(1));
                Assert.That(statistics.IndicesAfter, Is.EqualTo(6), "the reverse-winding triangle is retained");
                Assert.That(model.Mesh.VertexList, Has.Length.EqualTo(3));
            });

            var nullBone = Vertex(0, 0, 0);
            var emptyBone = Clone(nullBone);
            nullBone.BoneIndex = null!;
            emptyBone.BoneIndex = [];
            var thirdBoneVariant = Clone(nullBone);
            thirdBoneVariant.BoneIndex = [1];
            var nullVsEmpty = CreateModel([nullBone, emptyBone, thirdBoneVariant], [0, 1, 2]);
            var nullVsEmptyStats = RigidGeometryOptimizer.Optimize(nullVsEmpty);

            Assert.Multiple(() =>
            {
                Assert.That(nullVsEmptyStats.DuplicateVerticesRemoved, Is.Zero);
                Assert.That(nullVsEmptyStats.IndicesAfter, Is.EqualTo(3));
                Assert.That(nullVsEmpty.Mesh.VertexList, Has.Length.EqualTo(3));
            });

            var nullWeight = Vertex(0, 0, 0);
            var emptyWeight = Clone(nullWeight);
            var populatedWeight = Clone(nullWeight);
            nullWeight.BoneIndex = [0];
            emptyWeight.BoneIndex = [0];
            populatedWeight.BoneIndex = [0];
            nullWeight.BoneWeight = null!;
            emptyWeight.BoneWeight = [];
            populatedWeight.BoneWeight = [1f];
            var nullVsEmptyWeight = CreateModel([nullWeight, emptyWeight, populatedWeight], [0, 1, 2]);
            var nullVsEmptyWeightStats = RigidGeometryOptimizer.Optimize(nullVsEmptyWeight);
            Assert.Multiple(() =>
            {
                Assert.That(nullVsEmptyWeightStats.DuplicateVerticesRemoved, Is.Zero);
                Assert.That(nullVsEmptyWeightStats.IndicesAfter, Is.EqualTo(3));
            });
        }

        [Test]
        public void Optimize_UsesFirstReferencedRepresentativeForAnExactDuplicate()
        {
            var unusedEarlierCopy = Vertex(0, 0, 0);
            var firstReferencedCopy = Clone(unusedEarlierCopy);
            var b = Vertex(1, 0, 0);
            var c = Vertex(0, 1, 0);
            var model = CreateModel([unusedEarlierCopy, b, c, firstReferencedCopy], [3, 1, 2]);

            RigidGeometryOptimizer.Optimize(model);

            Assert.That(model.Mesh.VertexList.Any(vertex => ReferenceEquals(vertex, firstReferencedCopy)), Is.True);
            Assert.That(model.Mesh.VertexList.Any(vertex => ReferenceEquals(vertex, unusedEarlierCopy)), Is.False);
        }

        [Test]
        public void Optimize_AllFilteredWeightedPartSerializesAndReloadsAsEmpty()
        {
            var duplicate = WeightedVertex(0, 0, 0);
            var rigid = CreateWeightedRigid([duplicate, Clone(duplicate), Clone(duplicate)], [0, 1, 2]);
            var model = rigid.ModelList[0][0];

            var statistics = RigidGeometryOptimizer.Optimize(model);

            Assert.Multiple(() =>
            {
                Assert.That(statistics.IndicesAfter, Is.Zero);
                Assert.That(model.Mesh.IndexList, Is.Empty);
                Assert.That(model.Mesh.VertexList, Is.Empty);
            });

            rigid.RecalculateOffsets();
            var bytes = ModelFactory.Create().Save(rigid, validateByReloading: true, logProgress: false);
            var reloaded = ModelFactory.Create().Load(bytes).ModelList[0][0];
            Assert.Multiple(() =>
            {
                Assert.That(reloaded.Mesh.IndexList, Is.Empty);
                Assert.That(reloaded.Mesh.VertexList, Is.Empty);
                Assert.That(reloaded.Material.BinaryVertexFormat, Is.EqualTo(VertexFormat.Weighted));
            });
        }

        [Test]
        public void ModelFactory_RejectsContradictoryEmptyMeshHeaders()
        {
            var rigid = CreateWeightedRigid([], []);
            var bytesWithIndex = ModelFactory.Create().Save(rigid, validateByReloading: false, logProgress: false);
            var modelStart = RmvFileHeader.HeaderSize + (int)LodHeaderFactory.Create().GetHeaderSize(rigid.Header.Version);
            var commonHeader = ByteHelper.ByteArrayToStructure<RmvCommonHeader>(bytesWithIndex, modelStart);
            commonHeader.IndexCount = 1;
            Array.Copy(ByteHelper.GetBytes(commonHeader), 0, bytesWithIndex, modelStart, ByteHelper.GetSize<RmvCommonHeader>());

            Assert.Throws<InvalidDataException>(() => ModelFactory.Create().Load(bytesWithIndex));

            var bytesWithVertexSection = ModelFactory.Create().Save(rigid, validateByReloading: false, logProgress: false);
            commonHeader = ByteHelper.ByteArrayToStructure<RmvCommonHeader>(bytesWithVertexSection, modelStart);
            commonHeader.IndexOffset++;
            Array.Copy(ByteHelper.GetBytes(commonHeader), 0, bytesWithVertexSection, modelStart, ByteHelper.GetSize<RmvCommonHeader>());

            Assert.Throws<InvalidDataException>(() => ModelFactory.Create().Load(bytesWithVertexSection));
        }

        [Test]
        public void NativeOrdering_ReturnsAnEmptyIndexBufferWithoutVertices()
        {
            var result = MeshOptTriangleOrderOptimizer.OptimizeVertexCacheAndOverdraw([], []);

            Assert.That(result, Is.Empty);
        }

        [Test]
        public void NativeOrdering_PreservesSingleTriangleOrientation()
        {
            ushort[] indices = [2, 0, 1];
            float[] positions =
            [
                0, 0, 0,
                1, 0, 0,
                0, 1, 0,
            ];

            var result = MeshOptTriangleOrderOptimizer.OptimizeVertexCacheAndOverdraw(indices, positions);
            var signedArea = TriangleSignedArea(result, positions);

            Assert.Multiple(() =>
            {
                Assert.That(result, Has.Length.EqualTo(3));
                Assert.That(result, Is.EquivalentTo(indices));
                Assert.That(signedArea, Is.GreaterThan(0));
            });
        }

        [Test]
        public void NativeOrdering_RejectsMalformedAndOutOfRangeIndices()
        {
            float[] positions =
            [
                0, 0, 0,
                1, 0, 0,
                0, 1, 0,
            ];

            Assert.Multiple(() =>
            {
                Assert.Throws<ArgumentException>(() =>
                    MeshOptTriangleOrderOptimizer.OptimizeVertexCacheAndOverdraw([0, 1], positions));
                Assert.Throws<ArgumentException>(() =>
                    MeshOptTriangleOrderOptimizer.OptimizeVertexCacheAndOverdraw([0, 1, 3], positions));
            });
        }

        [Test]
        public void NativeOrdering_RejectsNonFinitePositions()
        {
            ushort[] indices = [0, 1, 2];
            float[] positions =
            [
                0, 0, 0,
                1, 0, 0,
                0, float.NaN, 0,
            ];

            Assert.Throws<ArgumentException>(() =>
                MeshOptTriangleOrderOptimizer.OptimizeVertexCacheAndOverdraw(indices, positions));
        }

        [Test]
        public void NativeVertexFetch_ReturnsFirstUsePermutationAndDropsUnusedVertices()
        {
            ushort[] indices = [3, 1, 2, 3, 2, 1];
            var originalIndices = (ushort[])indices.Clone();

            var result = MeshOptTriangleOrderOptimizer.OptimizeVertexFetch(indices, vertexCount: 5);

            Assert.Multiple(() =>
            {
                Assert.That(result.SourceVertexIndices, Is.EqualTo(new[] { 3, 1, 2 }));
                Assert.That(result.Indices, Is.EqualTo(new ushort[] { 0, 1, 2, 0, 2, 1 }));
                Assert.That(indices, Is.EqualTo(originalIndices));
            });
        }

        [Test]
        public void NativeVertexFetch_ReturnsNoVerticesForAnEmptyIndexBuffer()
        {
            var result = MeshOptTriangleOrderOptimizer.OptimizeVertexFetch([], vertexCount: 8);

            Assert.Multiple(() =>
            {
                Assert.That(result.Indices, Is.Empty);
                Assert.That(result.SourceVertexIndices, Is.Empty);
            });
        }

        [Test]
        public void NativeVertexFetch_HandlesTheFullUshortIndexRange()
        {
            ushort[] indices = [ushort.MaxValue, 0, 32768];

            var result = MeshOptTriangleOrderOptimizer.OptimizeVertexFetch(indices, vertexCount: ushort.MaxValue + 1);

            Assert.Multiple(() =>
            {
                Assert.That(result.SourceVertexIndices, Is.EqualTo(new[] { (int)ushort.MaxValue, 0, 32768 }));
                Assert.That(result.Indices, Is.EqualTo(new ushort[] { 0, 1, 2 }));
            });
        }

        [Test]
        public void NativeVertexFetch_RejectsMalformedAndOutOfRangeIndices()
        {
            Assert.Multiple(() =>
            {
                Assert.Throws<ArgumentException>(() =>
                    MeshOptTriangleOrderOptimizer.OptimizeVertexFetch([0, 1], vertexCount: 3));
                Assert.Throws<ArgumentException>(() =>
                    MeshOptTriangleOrderOptimizer.OptimizeVertexFetch([0, 1, 3], vertexCount: 3));
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    MeshOptTriangleOrderOptimizer.OptimizeVertexFetch([], vertexCount: ushort.MaxValue + 2));
            });
        }

        [Test]
        public void NativeVertexRemap_FindsExactFullKeyDuplicatesAndMarksUnusedVertices()
        {
            var keys = UIntKeys(10, 20, 30, 10, 40);

            var result = MeshOptTriangleOrderOptimizer.GenerateVertexRemap(
                [3, 1, 2, 0, 1, 2],
                keys,
                vertexCount: 5,
                vertexStride: 4);

            Assert.Multiple(() =>
            {
                Assert.That(result.UniqueVertexCount, Is.EqualTo(3));
                Assert.That(result.OldToNew[3], Is.EqualTo(result.OldToNew[0]));
                Assert.That(result.OldToNew[0], Is.Not.EqualTo(uint.MaxValue));
                Assert.That(result.OldToNew[1], Is.Not.EqualTo(uint.MaxValue));
                Assert.That(result.OldToNew[2], Is.Not.EqualTo(uint.MaxValue));
                Assert.That(result.OldToNew[4], Is.EqualTo(uint.MaxValue));
            });
        }

        [Test]
        public void NativeVertexRemap_HandlesEmptyInputAndTheFullUshortIndexRange()
        {
            var empty = MeshOptTriangleOrderOptimizer.GenerateVertexRemap([], [], vertexCount: 0, vertexStride: 4);
            var keys = UIntKeys(Enumerable.Range(0, ushort.MaxValue + 1).ToArray());
            ushort[] indices = [ushort.MaxValue, 0, 32768];

            var fullRange = MeshOptTriangleOrderOptimizer.GenerateVertexRemap(indices, keys, vertexCount: ushort.MaxValue + 1, vertexStride: 4);

            Assert.Multiple(() =>
            {
                Assert.That(empty.UniqueVertexCount, Is.Zero);
                Assert.That(empty.OldToNew, Is.Empty);
                Assert.That(fullRange.UniqueVertexCount, Is.EqualTo(3));
                Assert.That(fullRange.OldToNew[ushort.MaxValue], Is.Not.EqualTo(fullRange.OldToNew[0]));
                Assert.That(fullRange.OldToNew[0], Is.Not.EqualTo(fullRange.OldToNew[32768]));
                Assert.That(fullRange.OldToNew[1], Is.EqualTo(uint.MaxValue));
            });
        }

        [Test]
        public void NativeIndexFilter_RemovesFullKeyDegeneratesAndDuplicatesButKeepsReverseWinding()
        {
            var keys = UIntKeys(10, 20, 30, 10, 20, 30);
            ushort[] indices = [0, 1, 2, 1, 2, 0, 3, 4, 5, 0, 2, 1, 0, 0, 1];

            var filtered = MeshOptTriangleOrderOptimizer.FilterIndexBuffer(indices, keys, vertexCount: 6, vertexStride: 4);

            var outputKeys = new int[filtered.Length];
            for (var i = 0; i < filtered.Length; i++)
                outputKeys[i] = BinaryPrimitives.ReadInt32LittleEndian(keys.AsSpan(filtered[i] * 4, 4));

            Assert.Multiple(() =>
            {
                Assert.That(filtered, Has.Length.EqualTo(6));
                Assert.That(outputKeys, Is.EqualTo(new[] { 10, 20, 30, 10, 30, 20 }));
            });
        }

        [Test]
        public void NativeIndexFilter_ReturnsEmptyForEmptyOrFullyFilteredInput()
        {
            Assert.That(
                MeshOptTriangleOrderOptimizer.FilterIndexBuffer([], [], vertexCount: 0, vertexStride: 4),
                Is.Empty);
            Assert.That(
                MeshOptTriangleOrderOptimizer.FilterIndexBuffer([0, 1, 2], UIntKeys(1, 1, 1), vertexCount: 3, vertexStride: 4),
                Is.Empty);
        }

        [Test]
        public void NativeRemapAndFilter_RejectMalformedBuffersAndOutOfRangeIndices()
        {
            var keys = UIntKeys(1, 2, 3);

            Assert.Multiple(() =>
            {
                Assert.Throws<ArgumentException>(() => MeshOptTriangleOrderOptimizer.GenerateVertexRemap([0, 1], keys, 3, 4));
                Assert.Throws<ArgumentException>(() => MeshOptTriangleOrderOptimizer.GenerateVertexRemap([0, 1, 3], keys, 3, 4));
                Assert.Throws<ArgumentException>(() => MeshOptTriangleOrderOptimizer.FilterIndexBuffer([0, 1], keys, 3, 4));
                Assert.Throws<ArgumentException>(() => MeshOptTriangleOrderOptimizer.FilterIndexBuffer([0, 1, 3], keys, 3, 4));
                Assert.Throws<ArgumentException>(() => MeshOptTriangleOrderOptimizer.FilterIndexBuffer([0, 1, 2], keys[..^1], 3, 4));
                Assert.Throws<ArgumentOutOfRangeException>(() => MeshOptTriangleOrderOptimizer.GenerateVertexRemap([0, 1, 2], keys, 3, 260));
            });
        }

        private static RmvModel CreateModel(CommonVertex[] vertices, ushort[] indices)
            => new()
            {
                Mesh = new RmvMesh
                {
                    VertexList = vertices,
                    IndexList = indices,
                },
            };

        private static RmvFile CreateWeightedRigid(CommonVertex[] vertices, ushort[] indices)
        {
            const RmvVersionEnum version = RmvVersionEnum.RMV2_V8;
            var commonHeader = RmvCommonHeader.CreateDefault();
            commonHeader.ModelTypeFlag = ModelMaterialEnum.weighted;
            var model = new RmvModel
            {
                CommonHeader = commonHeader,
                Material = new WeightedMaterial
                {
                    BinaryVertexFormat = VertexFormat.Weighted,
                    MaterialId = ModelMaterialEnum.weighted,
                    ModelName = "part",
                    TextureDirectory = "tex",
                },
                Mesh = new RmvMesh
                {
                    VertexList = vertices,
                    IndexList = indices,
                },
            };

            var header = new RmvFileHeader
            {
                _fileType = "RMV2"u8.ToArray(),
                Version = version,
                LodCount = 1,
            };
            header.SkeletonName = "skeleton";
            var rigid = new RmvFile
            {
                Header = header,
                LodHeaders = [LodHeaderFactory.Create().CreateEmpty(version, 100f, 0, 0)],
                ModelList = [[model]],
            };
            rigid.RecalculateOffsets();
            return rigid;
        }

        private static CommonVertex Vertex(float x, float y, float z)
            => new()
            {
                Position = new Vector4(x, y, z, 1),
                Normal = Vector3.UnitZ,
                BiNormal = Vector3.UnitY,
                Tangent = Vector3.UnitX,
                Uv = Vector2.Zero,
                Uv1 = Vector2.Zero,
                Colour = Vector4.One,
                BoneIndex = [],
                BoneWeight = [],
                WeightCount = 0,
            };

        private static CommonVertex WeightedVertex(float x, float y, float z)
        {
            var vertex = Vertex(x, y, z);
            vertex.BoneIndex = [0, 1];
            vertex.BoneWeight = [1f, 0f];
            vertex.WeightCount = 2;
            return vertex;
        }

        private static CommonVertex Clone(CommonVertex source)
            => new()
            {
                Position = source.Position,
                Normal = source.Normal,
                BiNormal = source.BiNormal,
                Tangent = source.Tangent,
                Uv = source.Uv,
                Uv1 = source.Uv1,
                Colour = source.Colour,
                BoneIndex = source.BoneIndex?.ToArray()!,
                BoneWeight = source.BoneWeight?.ToArray()!,
                WeightCount = source.WeightCount,
            };

        private static byte[] UIntKeys(params int[] keys)
        {
            var bytes = new byte[checked(keys.Length * sizeof(uint))];
            for (var i = 0; i < keys.Length; i++)
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * sizeof(uint)), checked((uint)keys[i]));
            return bytes;
        }

        private static CommonVertex Vertex(float x, float y, float z, byte payloadId)
        {
            var payload = payloadId;
            return new CommonVertex
            {
                Position = new Vector4(x, y, z, 1 + payload),
                Normal = new Vector3(payload, payload + 1, payload + 2),
                BiNormal = new Vector3(payload + 3, payload + 4, payload + 5),
                Tangent = new Vector3(payload + 6, payload + 7, payload + 8),
                Uv = new Vector2(payload, payload + 0.25f),
                Uv1 = new Vector2(payload + 0.5f, payload + 0.75f),
                Colour = new Vector4(payload, payload + 1, payload + 2, payload + 3),
                BoneIndex = [payloadId],
                BoneWeight = [payload + 0.125f],
                WeightCount = 1,
            };
        }

        private static string[] TrianglePayloadKeys(RmvModel model)
            => TrianglePayloadKeys(model.Mesh.VertexList, model.Mesh.IndexList);

        private static string[] TrianglePayloadKeys(
            IReadOnlyList<CommonVertex> vertices,
            IReadOnlyList<ushort> indices)
        {
            var result = new string[indices.Count / 3];
            for (var i = 0; i < indices.Count; i += 3)
            {
                result[i / 3] = string.Join("|",
                    VertexPayloadKey(vertices[indices[i]]),
                    VertexPayloadKey(vertices[indices[i + 1]]),
                    VertexPayloadKey(vertices[indices[i + 2]]));
            }

            return result;
        }

        private static string VertexPayloadKey(CommonVertex vertex)
        {
            var boneIndices = vertex.BoneIndex == null
                ? "<null>"
                : Convert.ToHexString(vertex.BoneIndex);
            var boneWeights = vertex.BoneWeight == null
                ? "<null>"
                : string.Join(",", vertex.BoneWeight.Select(FloatBits));

            return string.Join("/",
                VectorKey(vertex.Position),
                VectorKey(vertex.Normal),
                VectorKey(vertex.BiNormal),
                VectorKey(vertex.Tangent),
                VectorKey(vertex.Uv),
                VectorKey(vertex.Uv1),
                VectorKey(vertex.Colour),
                boneIndices,
                boneWeights,
                vertex.WeightCount.ToString());
        }

        private static string VectorKey(Vector2 value)
            => $"{FloatBits(value.X)},{FloatBits(value.Y)}";

        private static string VectorKey(Vector3 value)
            => $"{FloatBits(value.X)},{FloatBits(value.Y)},{FloatBits(value.Z)}";

        private static string VectorKey(Vector4 value)
            => $"{FloatBits(value.X)},{FloatBits(value.Y)},{FloatBits(value.Z)},{FloatBits(value.W)}";

        private static int FloatBits(float value)
            => BitConverter.SingleToInt32Bits(value);

        private static float TriangleSignedArea(IReadOnlyList<ushort> indices, IReadOnlyList<float> positions)
        {
            var first = indices[0] * 3;
            var second = indices[1] * 3;
            var third = indices[2] * 3;
            return (positions[second] - positions[first]) * (positions[third + 1] - positions[first + 1]) -
                   (positions[second + 1] - positions[first + 1]) * (positions[third] - positions[first]);
        }

        private static string[] TrianglePositionKeys(RmvModel model)
        {
            var result = new List<string>();
            for (var i = 0; i < model.Mesh.IndexList.Length; i += 3)
            {
                var triangle = new[]
                {
                    PositionKey(model.Mesh.VertexList[model.Mesh.IndexList[i]]),
                    PositionKey(model.Mesh.VertexList[model.Mesh.IndexList[i + 1]]),
                    PositionKey(model.Mesh.VertexList[model.Mesh.IndexList[i + 2]]),
                };
                result.Add(string.Join("|", triangle));
            }

            return result.ToArray();
        }

        private static string PositionKey(CommonVertex vertex)
            => $"{vertex.Position.X},{vertex.Position.Y},{vertex.Position.Z}";
    }
}
