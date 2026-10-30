using System.Collections;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Shared.ByteParsing
{
    public class ByteHelper
    {
        public static T ByteArrayToStructure<T>(byte[] bytes, int offset) where T : struct
        {
            var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);

            var objectSize = GetSize<T>();
            if (offset + objectSize > bytes.Length)
                throw new Exception($"Object {typeof(T)} does not fit into the remaining buffer [offset{offset} + Size{objectSize} => byteBuffer{bytes.Length}]");

            try
            {
                var p = handle.AddrOfPinnedObject() + offset;
                var pointerToStruct = Marshal.PtrToStructure(p, typeof(T));
                if (pointerToStruct == null)
                    throw new Exception("Object is null after getting pointerToStruct");
                return (T)pointerToStruct;
            }
            finally
            {
                handle.Free();
            }
        }


        public static ReadOnlySpan<T> LoadArray<T>(byte[] bytes, int offset, int totalBytesToRead) where T:struct
        {
            var span = bytes.AsSpan(offset, totalBytesToRead);
            var structSpan = MemoryMarshal.Cast<byte, T>(span);
            return structSpan;
        }

        public static byte[] GetBytes<T>(T data) where T : struct
        {
            if (BlittableLayout<T>.IsBlittable)
            {
                // Same bytes as Marshal.StructureToPtr for a blittable struct, without the native allocation.
                var blittableBytes = new byte[Unsafe.SizeOf<T>()];
                Unsafe.WriteUnaligned(ref blittableBytes[0], data);
                return blittableBytes;
            }

            var size = Marshal.SizeOf(data);
            var arr = new byte[size];

            var ptr = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(data, ptr, true);
            Marshal.Copy(ptr, arr, 0, size);
            Marshal.FreeHGlobal(ptr);
            return arr;
        }

        private static class BlittableLayout<T> where T : struct
        {
            public static readonly bool IsBlittable = Probe();

            private static bool Probe()
            {
                if (RuntimeHelpers.IsReferenceOrContainsReferences<T>() || Unsafe.SizeOf<T>() == 0)
                    return false;

                try
                {
                    // Pinning a boxed instance only succeeds for blittable types.
                    GCHandle.Alloc(default(T), GCHandleType.Pinned).Free();
                    return Marshal.SizeOf<T>() == Unsafe.SizeOf<T>();
                }
                catch (ArgumentException)
                {
                    return false;
                }
            }
        }

        static public byte[] CreateFixLengthString(string str, int maxLength)
        {
            var output = new byte[maxLength];
            var byteValues = Encoding.UTF8.GetBytes(str);
            for (var i = 0; i < byteValues.Length && i < maxLength; i++)
                output[i] = byteValues[i];
            return output;
        }

        public static int GetSize(Type type)
        {
            return Marshal.SizeOf(type);
        }

        // Keep Marshal.SizeOf failures inside Lazy.Value so callers receive the original exception.
        private static class MarshaledSize<T>
        {
            internal static readonly Lazy<int> Size =
                new(() => Marshal.SizeOf(typeof(T)));
        }

        public static int GetSize<T>()
        {
            return MarshaledSize<T>.Size.Value;
        }

        public static uint GetPropertyTypeSize<T>(T property)
        {
            if (property == null)
            {
                var type = typeof(T);
                type = Nullable.GetUnderlyingType(type) ?? type;
                return (uint)Marshal.SizeOf(type);
            }

            if (property is IList)
                return (uint)Marshal.SizeOf(typeof(T).GetGenericArguments()[0]);

            if (property is Enum)
                return (uint)Marshal.SizeOf(Enum.GetUnderlyingType(property.GetType()));

            return (uint)Marshal.SizeOf(property);
        }
    }
}
