using System.Runtime.InteropServices;

namespace Leaf.Pdfium.Native;

/// <summary>
/// Receives a saved PDF from pdfium through FPDF_FILEWRITE and forwards it to a managed stream.
/// The unmanaged struct stays valid until <see cref="Dispose"/>, which must run after the save call returns.
/// </summary>
/// <remarks>
/// Unlike FPDF_FILEACCESS there is no user-data field on FPDF_FILEWRITE: pdfium passes the struct pointer
/// itself back to the callback. So the block is over-allocated and the GCHandle lives immediately after the
/// two fields pdfium knows about, where it can be recovered from the pointer alone.
/// </remarks>
internal sealed unsafe class PdfFileSink : IDisposable
{
    private readonly Stream _stream;
    private readonly GCHandle _self;
    private readonly FPDF_FILEWRITE* _write;
    private volatile bool _faulted;

    public PdfFileSink(Stream stream)
    {
        _stream = stream;
        _self = GCHandle.Alloc(this);
        _write = (FPDF_FILEWRITE*)NativeMemory.AllocZeroed((nuint)(sizeof(FPDF_FILEWRITE) + sizeof(nint)));
        _write->Version = 1;
        _write->WriteBlock = &WriteBlock;
        *HandleSlot(_write) = GCHandle.ToIntPtr(_self);
    }

    public FPDF_FILEWRITE* Write => _write;

    /// <summary>True once a write failed (disk full, path removed); the output must then be discarded.</summary>
    public bool Faulted => _faulted;

    private static nint* HandleSlot(FPDF_FILEWRITE* write) => (nint*)((byte*)write + sizeof(FPDF_FILEWRITE));

    [UnmanagedCallersOnly]
    private static int WriteBlock(FPDF_FILEWRITE* self, void* data, uint size)
    {
        try
        {
            var sink = (PdfFileSink)GCHandle.FromIntPtr(*HandleSlot(self)).Target!;
            if (size > 0)
            {
                sink._stream.Write(new ReadOnlySpan<byte>(data, checked((int)size)));
            }

            return 1;
        }
        catch
        {
            // Must not let an exception cross the native boundary.
            try
            {
                ((PdfFileSink)GCHandle.FromIntPtr(*HandleSlot(self)).Target!)._faulted = true;
            }
            catch
            {
                // ignore
            }

            return 0;
        }
    }

    public void Dispose()
    {
        NativeMemory.Free(_write);
        _self.Free();
    }
}
