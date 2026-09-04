using System.Runtime.InteropServices;

namespace Leaf.Pdfium.Native;

/// <summary>
/// Feeds pdfium through FPDF_FILEACCESS from a FileStream that does NOT lock the file
/// (other programs may overwrite or delete it while it is open; reads then fail cleanly).
/// The unmanaged struct stays valid until <see cref="Dispose"/>, which must run after FPDF_CloseDocument.
/// </summary>
internal sealed unsafe class PdfFileSource : IDisposable
{
    private readonly FileStream _stream;
    private readonly GCHandle _self;
    private readonly FPDF_FILEACCESS* _access;
    private volatile bool _faulted;

    public PdfFileSource(string path)
    {
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.RandomAccess);
        if (_stream.Length > uint.MaxValue)
        {
            _stream.Dispose();
            throw new PdfException(PdfError.File, "Files of 4 GiB or more are not supported.");
        }

        _self = GCHandle.Alloc(this);
        _access = (FPDF_FILEACCESS*)NativeMemory.AllocZeroed((nuint)sizeof(FPDF_FILEACCESS));
        _access->FileLen = (uint)_stream.Length;
        _access->GetBlock = &GetBlock;
        _access->Param = (void*)GCHandle.ToIntPtr(_self);
    }

    public FPDF_FILEACCESS* Access => _access;

    public long Length => _access->FileLen;

    /// <summary>True once a read failed (file changed or vanished underneath us).</summary>
    public bool Faulted => _faulted;

    [UnmanagedCallersOnly]
    private static int GetBlock(void* param, uint position, byte* buffer, uint size)
    {
        try
        {
            var self = (PdfFileSource)GCHandle.FromIntPtr((nint)param).Target!;
            self._stream.Position = position;
            self._stream.ReadExactly(new Span<byte>(buffer, (int)size));
            return 1;
        }
        catch
        {
            // Must not let an exception cross the native boundary.
            try
            {
                ((PdfFileSource)GCHandle.FromIntPtr((nint)param).Target!)._faulted = true;
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
        NativeMemory.Free(_access);
        _self.Free();
        _stream.Dispose();
    }
}
