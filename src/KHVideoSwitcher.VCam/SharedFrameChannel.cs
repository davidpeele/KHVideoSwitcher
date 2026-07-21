using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace KHVideoSwitcher.VCam
{
    /// <summary>
    /// Cross-process BGRA frame channel over a named shared-memory section.
    ///
    /// The switcher app (user session) writes frames; the virtual camera media
    /// source (loaded into the Windows Frame Server service, and also in-proc
    /// into consumer apps) reads them. A seqlock protects against torn frames:
    /// the writer makes the sequence odd while writing and even when stable;
    /// readers retry if the sequence changed mid-copy.
    ///
    /// The section lives in the Global\ namespace so it spans sessions. Any
    /// party may create it (create-or-open); it is created with a DACL that
    /// grants Everyone + ALL APPLICATION PACKAGES access so that the service,
    /// the user app, and sandboxed consumers can all map it.
    /// </summary>
    public sealed unsafe class SharedFrameChannel : IDisposable
    {
        public const string SectionName = "Global\\KHVideoSwitcher.VCam.Frames.v1";
        public const uint Magic = 0x4348564B; // 'KVHC' little-endian tag
        public const int MaxWidth = 1920;
        public const int MaxHeight = 1080;
        public const int HeaderSize = 64;
        public const int PixelCapacity = MaxWidth * MaxHeight * 4;
        public const int Capacity = HeaderSize + PixelCapacity;

        // Header layout (offsets in bytes):
        //  0 uint  magic
        //  4 int   width
        //  8 int   height
        // 12 int   stride
        // 16 long  seq   (odd = write in progress)
        // 24 long  timestamp (QPC ticks, informational)
        private IntPtr _mapping;
        private byte* _view;

        public bool IsOpen => _view != null;

        private uint* PMagic => (uint*)_view;
        private int* PWidth => (int*)(_view + 4);
        private int* PHeight => (int*)(_view + 8);
        private int* PStride => (int*)(_view + 12);
        private long* PSeq => (long*)(_view + 16);
        private long* PTimestamp => (long*)(_view + 24);
        private byte* PPixels => _view + HeaderSize;

        /// <summary>Try to open (or create) the shared section. Safe to call repeatedly until it succeeds.</summary>
        public bool TryOpen()
        {
            if (IsOpen)
                return true;

            // Open first: only privileged processes (services) can create Global\ sections.
            var mapping = OpenFileMapping(FILE_MAP_ALL_ACCESS, false, SectionName);
            if (mapping == IntPtr.Zero)
            {
                // Grant Everyone (WD) and ALL APPLICATION PACKAGES (AC) full access.
                IntPtr sd = IntPtr.Zero;
                SECURITY_ATTRIBUTES sa = default;
                sa.nLength = (uint)Marshal.SizeOf<SECURITY_ATTRIBUTES>();
                if (ConvertStringSecurityDescriptorToSecurityDescriptorW(
                        "D:(A;;GA;;;WD)(A;;GA;;;AC)", 1, out sd, out _))
                {
                    sa.lpSecurityDescriptor = sd;
                }

                try
                {
                    mapping = CreateFileMappingW(new IntPtr(-1), sa.lpSecurityDescriptor != IntPtr.Zero ? &sa : null,
                        PAGE_READWRITE, 0, (uint)Capacity, SectionName);
                }
                finally
                {
                    if (sd != IntPtr.Zero)
                        LocalFree(sd);
                }

                if (mapping == IntPtr.Zero)
                    return false;
            }

            var view = MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, UIntPtr.Zero);
            if (view == IntPtr.Zero)
            {
                CloseHandle(mapping);
                return false;
            }

            _mapping = mapping;
            _view = (byte*)view;
            return true;
        }

        /// <summary>
        /// Publishes a frame. <paramref name="copyPixels"/> receives the destination
        /// pointer and must copy exactly <paramref name="stride"/> * <paramref name="height"/> bytes.
        /// </summary>
        public bool WriteFrame(int width, int height, int stride, Action<IntPtr> copyPixels)
        {
            if (!IsOpen || width <= 0 || height <= 0 || stride * height > PixelCapacity)
                return false;

            var seq = Interlocked.Read(ref *PSeq);
            Interlocked.Exchange(ref *PSeq, seq + 1); // odd: write in progress
            *PWidth = width;
            *PHeight = height;
            *PStride = stride;
            *PTimestamp = Environment.TickCount64;
            copyPixels((IntPtr)PPixels);
            *PMagic = Magic;
            Interlocked.Exchange(ref *PSeq, seq + 2); // even: stable
            return true;
        }

        /// <summary>
        /// Copies the latest stable frame into <paramref name="dest"/> (BGRA, tightly packed).
        /// Returns false if no valid frame is available or it is too stale.
        /// </summary>
        public bool TryReadFrame(byte[] dest, out int width, out int height, out long ageMs)
        {
            width = 0;
            height = 0;
            ageMs = long.MaxValue;
            if (!IsOpen || *PMagic != Magic)
                return false;

            for (var attempt = 0; attempt < 3; attempt++)
            {
                var seqBefore = Interlocked.Read(ref *PSeq);
                if (seqBefore == 0 || (seqBefore & 1) != 0)
                {
                    Thread.SpinWait(100);
                    continue;
                }

                int w = *PWidth, h = *PHeight, stride = *PStride;
                long ts = *PTimestamp;
                if (w <= 0 || h <= 0 || stride < w * 4 || stride * h > PixelCapacity || dest.Length < w * h * 4)
                    return false;

                fixed (byte* pDest = dest)
                {
                    if (stride == w * 4)
                    {
                        Buffer.MemoryCopy(PPixels, pDest, dest.Length, (long)stride * h);
                    }
                    else
                    {
                        for (var y = 0; y < h; y++)
                            Buffer.MemoryCopy(PPixels + (long)y * stride, pDest + (long)y * w * 4, w * 4, w * 4);
                    }
                }

                if (Interlocked.Read(ref *PSeq) != seqBefore)
                    continue; // torn read; retry

                width = w;
                height = h;
                ageMs = Environment.TickCount64 - ts;
                return true;
            }
            return false;
        }

        public void Dispose()
        {
            if (_view != null)
            {
                UnmapViewOfFile((IntPtr)_view);
                _view = null;
            }
            if (_mapping != IntPtr.Zero)
            {
                CloseHandle(_mapping);
                _mapping = IntPtr.Zero;
            }
        }

        private const uint PAGE_READWRITE = 0x04;
        private const uint FILE_MAP_ALL_ACCESS = 0x000F001F;

        [StructLayout(LayoutKind.Sequential)]
        private struct SECURITY_ATTRIBUTES
        {
            public uint nLength;
            public IntPtr lpSecurityDescriptor;
            public int bInheritHandle;
        }

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileMappingW(IntPtr hFile, SECURITY_ATTRIBUTES* lpAttributes, uint flProtect, uint dwMaximumSizeHigh, uint dwMaximumSizeLow, string lpName);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenFileMapping(uint dwDesiredAccess, bool bInheritHandle, string lpName);

        [DllImport("kernel32", SetLastError = true)]
        private static extern IntPtr MapViewOfFile(IntPtr hFileMappingObject, uint dwDesiredAccess, uint dwFileOffsetHigh, uint dwFileOffsetLow, UIntPtr dwNumberOfBytesToMap);

        [DllImport("kernel32", SetLastError = true)]
        private static extern bool UnmapViewOfFile(IntPtr lpBaseAddress);

        [DllImport("kernel32", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("advapi32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string StringSecurityDescriptor, uint StringSDRevision, out IntPtr SecurityDescriptor, out uint SecurityDescriptorSize);

        [DllImport("kernel32")]
        private static extern IntPtr LocalFree(IntPtr hMem);
    }
}
