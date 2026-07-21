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
    /// into consumer apps) reads them.
    ///
    /// Double-buffered: the writer fills the inactive slot, then flips the
    /// active-slot index. Readers copy from the active slot, which the writer
    /// won't touch again until the next flip — so a reader can only tear if it
    /// falls a full frame interval behind mid-copy, and even then the per-slot
    /// sequence check catches it.
    ///
    /// The section lives in the Global\ namespace so it spans sessions. Any
    /// party may create it (create-or-open); it is created with a DACL that
    /// grants Everyone + ALL APPLICATION PACKAGES access so that the service,
    /// the user app, and sandboxed consumers can all map it.
    /// </summary>
    public sealed unsafe class SharedFrameChannel : IDisposable
    {
        public const string SectionName = "Global\\KHVideoSwitcher.VCam.Frames.v2";
        public const uint Magic = 0x4348564B;
        public const int MaxWidth = 1920;
        public const int MaxHeight = 1080;
        public const int HeaderSize = 128;
        public const int SlotMetaSize = 32;
        public const int PixelCapacity = MaxWidth * MaxHeight * 4;
        public const int Capacity = HeaderSize + 2 * PixelCapacity;

        // Layout:
        //   0  uint magic
        //   4  int  activeSlot (0 or 1)
        //   8  ...  reserved
        //  16  slot0 meta: int width, int height, int stride, int _pad, long seq, long timestamp
        //  48  slot1 meta: same
        // 128  slot0 pixels
        // 128 + PixelCapacity  slot1 pixels
        private IntPtr _mapping;
        private byte* _view;

        public bool IsOpen => _view != null;

        /// <summary>Win32 error from the last failed OpenFileMapping (0 = none).</summary>
        public int LastOpenError { get; private set; }

        /// <summary>Win32 error from the last failed CreateFileMapping (0 = none).</summary>
        public int LastCreateError { get; private set; }

        private uint* PMagic => (uint*)_view;
        private int* PActiveSlot => (int*)(_view + 4);

        private int* SlotWidth(int s) => (int*)(_view + 16 + s * SlotMetaSize);
        private int* SlotHeight(int s) => (int*)(_view + 16 + s * SlotMetaSize + 4);
        private int* SlotStride(int s) => (int*)(_view + 16 + s * SlotMetaSize + 8);
        private long* SlotSeq(int s) => (long*)(_view + 16 + s * SlotMetaSize + 16);
        private long* SlotTimestamp(int s) => (long*)(_view + 16 + s * SlotMetaSize + 24);
        private byte* SlotPixels(int s) => _view + HeaderSize + (long)s * PixelCapacity;

        /// <summary>Try to open (or create) the shared section. Safe to call repeatedly until it succeeds.</summary>
        public bool TryOpen()
        {
            if (IsOpen)
                return true;

            var mapping = OpenFileMapping(FILE_MAP_ALL_ACCESS, false, SectionName);
            if (mapping == IntPtr.Zero)
            {
                LastOpenError = Marshal.GetLastWin32Error();
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
                {
                    LastCreateError = Marshal.GetLastWin32Error();
                    return false;
                }
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
        /// Publishes a frame into the inactive slot, then flips it active.
        /// <paramref name="copyPixels"/> receives the destination pointer and must
        /// copy exactly <paramref name="stride"/> * <paramref name="height"/> bytes.
        /// </summary>
        public bool WriteFrame(int width, int height, int stride, Action<IntPtr> copyPixels)
        {
            if (!IsOpen || width <= 0 || height <= 0 || stride * height > PixelCapacity)
                return false;

            int target = 1 - Volatile.Read(ref *PActiveSlot);
            if (target != 0 && target != 1)
                target = 0;

            var seq = Interlocked.Read(ref *SlotSeq(target));
            Interlocked.Exchange(ref *SlotSeq(target), seq + 1); // odd: writing
            *SlotWidth(target) = width;
            *SlotHeight(target) = height;
            *SlotStride(target) = stride;
            *SlotTimestamp(target) = Environment.TickCount64;
            copyPixels((IntPtr)SlotPixels(target));
            Interlocked.Exchange(ref *SlotSeq(target), seq + 2); // even: stable

            *PMagic = Magic;
            Interlocked.Exchange(ref *PActiveSlot, target);
            return true;
        }

        /// <summary>
        /// Copies the latest stable frame into <paramref name="dest"/> (BGRA, tightly packed).
        /// Returns false if no valid frame is available.
        /// </summary>
        public bool TryReadFrame(byte[] dest, out int width, out int height, out long ageMs)
        {
            width = 0;
            height = 0;
            ageMs = long.MaxValue;
            if (!IsOpen || *PMagic != Magic)
                return false;

            for (var attempt = 0; attempt < 4; attempt++)
            {
                int slot = Volatile.Read(ref *PActiveSlot);
                if (slot != 0 && slot != 1)
                    return false;

                var seqBefore = Interlocked.Read(ref *SlotSeq(slot));
                if (seqBefore == 0 || (seqBefore & 1) != 0)
                {
                    // Writer mid-flip; the other slot may still be good.
                    slot = 1 - slot;
                    seqBefore = Interlocked.Read(ref *SlotSeq(slot));
                    if (seqBefore == 0 || (seqBefore & 1) != 0)
                    {
                        Thread.Sleep(0);
                        continue;
                    }
                }

                int w = *SlotWidth(slot), h = *SlotHeight(slot), stride = *SlotStride(slot);
                long ts = *SlotTimestamp(slot);
                if (w <= 0 || h <= 0 || stride < w * 4 || stride * h > PixelCapacity || dest.Length < w * h * 4)
                    return false;

                fixed (byte* pDest = dest)
                {
                    if (stride == w * 4)
                    {
                        Buffer.MemoryCopy(SlotPixels(slot), pDest, dest.Length, (long)stride * h);
                    }
                    else
                    {
                        for (var y = 0; y < h; y++)
                            Buffer.MemoryCopy(SlotPixels(slot) + (long)y * stride, pDest + (long)y * w * 4, w * 4, w * 4);
                    }
                }

                if (Interlocked.Read(ref *SlotSeq(slot)) != seqBefore)
                    continue; // extremely slow read overlapped two writes; retry

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
