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
    /// into consumer apps such as Zoom or a browser) reads them.
    ///
    /// Double-buffered: the writer fills the inactive slot, then flips the
    /// active-slot index. Readers copy from the active slot, which the writer
    /// won't touch again until the next flip — so a reader can only tear if it
    /// falls a full frame interval behind mid-copy, and even then the per-slot
    /// sequence check catches it.
    ///
    /// SECURITY — this is a trust boundary. The section lives in the Global\
    /// namespace so it spans sessions (the Frame Server runs in session 0), which
    /// means its contents are reachable by other local processes:
    ///
    ///  * WRITE is granted to Authenticated Users; Everyone and ALL APPLICATION
    ///    PACKAGES get READ only. This notably denies write access to sandboxed
    ///    AppContainer processes (e.g. browser content processes), which is the
    ///    path that would otherwise turn a browser-sandbox compromise into memory
    ///    corruption inside the Frame Server or another consumer. Low-integrity
    ///    processes are additionally blocked from writing by the default mandatory
    ///    integrity policy.
    ///  * Write access cannot be narrowed further than Authenticated Users: only a
    ///    process holding SeCreateGlobalPrivilege (i.e. the Frame Server service)
    ///    can create a Global\ section at all, so the service creates it and the
    ///    user-mode app must still be able to write into what the service made.
    ///    Same-user processes therefore remain able to write — which is not a
    ///    security boundary on Windows in any case.
    ///  * Consequently the header is treated as UNTRUSTED input. Every geometry
    ///    field is re-validated against MaxWidth/MaxHeight in 64-bit arithmetic
    ///    before any pointer math, and every copy is given the true remaining
    ///    destination capacity, so header values can never steer a read out of
    ///    bounds no matter who wrote them. Readers additionally map the view
    ///    read-only and use plain aligned loads (never Interlocked, which can
    ///    compare-exchange) so they cannot fault or write through it.
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
        private bool _writable;
        private readonly string _sectionName;

        public SharedFrameChannel() : this(SectionName)
        {
        }

        /// <summary>
        /// Alternate section name. Intended for tests, which cannot create a
        /// Global\ section without SeCreateGlobalPrivilege and so use a Local\ one.
        /// </summary>
        public SharedFrameChannel(string sectionName)
        {
            _sectionName = string.IsNullOrEmpty(sectionName) ? SectionName : sectionName;
        }

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

        /// <summary>
        /// Opens the channel for writing, creating the section if needed.
        /// Only the switcher app calls this. Safe to call repeatedly until it succeeds.
        /// </summary>
        public bool TryOpenForWrite()
        {
            if (IsOpen)
                return _writable;
            return Open(write: true);
        }

        /// <summary>
        /// Opens the channel read-only, creating the section if it doesn't exist
        /// yet (the media source usually wins this race, since only a process with
        /// SeCreateGlobalPrivilege can create a Global\ section at all).
        /// Safe to call repeatedly until it succeeds.
        /// </summary>
        public bool TryOpenForRead()
        {
            if (IsOpen)
                return true;
            return Open(write: false);
        }

        private bool Open(bool write)
        {
            uint access = write ? FILE_MAP_ALL_ACCESS : FILE_MAP_READ;

            var mapping = OpenFileMapping(access, false, _sectionName);
            if (mapping == IntPtr.Zero)
            {
                LastOpenError = Marshal.GetLastWin32Error();
                mapping = CreateSection(_sectionName);
                if (mapping == IntPtr.Zero)
                {
                    LastCreateError = Marshal.GetLastWin32Error();
                    return false;
                }
            }

            // Map an explicit Capacity rather than "whole section": if some other
            // process pre-created a smaller section under this name, the mapping
            // fails here instead of handing us a short buffer to run off the end of.
            var view = MapViewOfFile(mapping, access, 0, 0, (UIntPtr)Capacity);
            if (view == IntPtr.Zero)
            {
                CloseHandle(mapping);
                return false;
            }

            _mapping = mapping;
            _view = (byte*)view;
            _writable = write;
            return true;
        }

        private static IntPtr CreateSection(string sectionName)
        {
            // Authenticated Users (the switcher app and the Frame Server service):
            // full control. Everyone and ALL APPLICATION PACKAGES: read only, so
            // sandboxed consumers can display the feed but cannot write into it.
            const string sddl = "D:(A;;GA;;;AU)(A;;GR;;;WD)(A;;GR;;;AC)";

            IntPtr sd = IntPtr.Zero;
            SECURITY_ATTRIBUTES sa = default;
            sa.nLength = (uint)Marshal.SizeOf<SECURITY_ATTRIBUTES>();
            if (ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out sd, out _))
                sa.lpSecurityDescriptor = sd;

            try
            {
                return CreateFileMappingW(new IntPtr(-1),
                    sa.lpSecurityDescriptor != IntPtr.Zero ? &sa : null,
                    PAGE_READWRITE, 0, (uint)Capacity, sectionName);
            }
            finally
            {
                if (sd != IntPtr.Zero)
                    LocalFree(sd);
            }
        }

        /// <summary>
        /// Publishes a frame into the inactive slot, then flips it active.
        /// <paramref name="copyPixels"/> receives the destination pointer and must
        /// copy exactly <paramref name="stride"/> * <paramref name="height"/> bytes.
        /// </summary>
        public bool WriteFrame(int width, int height, int stride, Action<IntPtr> copyPixels)
        {
            if (!IsOpen || !_writable)
                return false;
            if (!IsValidGeometry(width, height, stride))
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
        /// Validates frame geometry entirely in 64-bit arithmetic. Bounding width
        /// and height first means none of the later products can overflow.
        /// </summary>
        private static bool IsValidGeometry(long width, long height, long stride)
        {
            if (width <= 0 || width > MaxWidth)
                return false;
            if (height <= 0 || height > MaxHeight)
                return false;
            if (stride < width * 4)
                return false;
            return stride * height <= PixelCapacity;
        }

        /// <summary>
        /// Copies the latest stable frame into <paramref name="dest"/> (BGRA, tightly packed).
        /// Returns false if no valid frame is available. All header fields are treated
        /// as untrusted.
        /// </summary>
        public bool TryReadFrame(byte[] dest, out int width, out int height, out long ageMs)
        {
            width = 0;
            height = 0;
            ageMs = long.MaxValue;
            if (dest is null || !IsOpen || Volatile.Read(ref *PMagic) != Magic)
                return false;

            for (var attempt = 0; attempt < 4; attempt++)
            {
                int slot = Volatile.Read(ref *PActiveSlot);
                if (slot != 0 && slot != 1)
                    return false;

                // Plain aligned 64-bit loads: this mapping may be read-only, so we
                // must not use Interlocked here (it can compare-exchange, i.e. write).
                long seqBefore = Volatile.Read(ref *SlotSeq(slot));
                if (seqBefore == 0 || (seqBefore & 1) != 0)
                {
                    // Writer mid-flip; the other slot may still be good.
                    slot = 1 - slot;
                    seqBefore = Volatile.Read(ref *SlotSeq(slot));
                    if (seqBefore == 0 || (seqBefore & 1) != 0)
                    {
                        Thread.Sleep(0);
                        continue;
                    }
                }

                long w = *SlotWidth(slot);
                long h = *SlotHeight(slot);
                long stride = *SlotStride(slot);
                long ts = Volatile.Read(ref *SlotTimestamp(slot));

                if (!IsValidGeometry(w, h, stride))
                    return false;

                long rowBytes = w * 4;
                long needed = rowBytes * h;
                if (needed > dest.LongLength)
                    return false;

                fixed (byte* pDest = dest)
                {
                    if (stride == rowBytes)
                    {
                        Buffer.MemoryCopy(SlotPixels(slot), pDest, dest.LongLength, needed);
                    }
                    else
                    {
                        for (long y = 0; y < h; y++)
                        {
                            long destOffset = y * rowBytes;
                            // Pass the REAL remaining capacity, not just the row size,
                            // so MemoryCopy's own bounds check is meaningful.
                            Buffer.MemoryCopy(SlotPixels(slot) + y * stride,
                                              pDest + destOffset,
                                              dest.LongLength - destOffset,
                                              rowBytes);
                        }
                    }
                }

                if (Volatile.Read(ref *SlotSeq(slot)) != seqBefore)
                    continue; // extremely slow read overlapped two writes; retry

                width = (int)w;
                height = (int)h;
                ageMs = Environment.TickCount64 - ts;
                return true;
            }
            return false;
        }

        /// <summary>
        /// TEST ONLY. Stamps arbitrary geometry into the active slot header to
        /// simulate a malicious process with write access, so the reader's
        /// validation can be exercised. Never call this from application code.
        /// </summary>
        public void DangerousOverwriteActiveGeometryForTesting(int width, int height, int stride)
        {
            if (!IsOpen || !_writable)
                return;
            int slot = Volatile.Read(ref *PActiveSlot);
            if (slot != 0 && slot != 1)
                return;
            *SlotWidth(slot) = width;
            *SlotHeight(slot) = height;
            *SlotStride(slot) = stride;
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
            _writable = false;
        }

        private const uint PAGE_READWRITE = 0x04;
        private const uint FILE_MAP_ALL_ACCESS = 0x000F001F;
        private const uint FILE_MAP_READ = 0x0004;

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
