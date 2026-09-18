namespace Shared.Core.PackFiles.Models.FileSources
{
    internal sealed class BoundedReadStream : Stream
    {
        private readonly Stream _baseStream;
        private readonly long _offset;
        private readonly long _length;
        private long _position;
        private bool _disposed;

        public BoundedReadStream(Stream baseStream, long offset, long length)
        {
            ArgumentNullException.ThrowIfNull(baseStream);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegative(length);

            if (!baseStream.CanRead)
                throw new ArgumentException("The base stream must be readable.", nameof(baseStream));
            if (!baseStream.CanSeek)
                throw new ArgumentException("The base stream must be seekable.", nameof(baseStream));

            _baseStream = baseStream;
            _offset = offset;
            _length = length;
            _baseStream.Seek(_offset, SeekOrigin.Begin);
        }

        public override bool CanRead => !_disposed && _baseStream.CanRead;
        public override bool CanSeek => !_disposed && _baseStream.CanSeek;
        public override bool CanWrite => false;
        public override long Length
        {
            get
            {
                ThrowIfDisposed();
                return _length;
            }
        }

        public override long Position
        {
            get
            {
                ThrowIfDisposed();
                return _position;
            }
            set => Seek(value, SeekOrigin.Begin);
        }

        public override void Flush() => ThrowIfDisposed();

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            ThrowIfDisposed();

            var remaining = _length - _position;
            if (remaining <= 0 || buffer.Length == 0)
                return 0;

            var bytesToRead = (int)Math.Min((long)buffer.Length, remaining);
            var bytesRead = _baseStream.Read(buffer[..bytesToRead]);
            _position += bytesRead;
            return bytesRead;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            ThrowIfDisposed();

            var targetPosition = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_position + offset),
                SeekOrigin.End => checked(_length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };

            if (targetPosition < 0 || targetPosition > _length)
                throw new IOException("The requested position is outside the bounded stream.");

            _baseStream.Seek(checked(_offset + targetPosition), SeekOrigin.Begin);
            _position = targetPosition;
            return _position;
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            _disposed = true;
            // The shared pack stream belongs to the caller and must remain open.
            base.Dispose(disposing);
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }
}
