namespace Leaf.Pdfium;

public enum PdfError
{
    Unknown = 1,
    File = 2,
    Format = 3,
    Password = 4,
    Security = 5,
    Page = 6,

    /// <summary>Writing a document failed. Deliberately outside 1..6: those mirror FPDF_ERR_* and are cast straight from FPDF_GetLastError.</summary>
    Write = 100,
}

/// <summary>Any failure reported by the engine. Never lets a bad PDF crash the process.</summary>
public sealed class PdfException : Exception
{
    public PdfException(PdfError error, string message)
        : base(message)
    {
        Error = error;
    }

    public PdfError Error { get; }

    internal static PdfException FromLastError(uint code, string? context = null)
    {
        PdfError error = code is >= 1 and <= 6 ? (PdfError)code : PdfError.Unknown;
        string text = error switch
        {
            PdfError.File => "The file could not be read.",
            PdfError.Format => "The file is not a valid PDF or is damaged.",
            PdfError.Password => "This PDF is password protected.",
            PdfError.Security => "The PDF uses an unsupported security scheme.",
            PdfError.Page => "The page could not be loaded.",
            _ => "The PDF could not be opened.",
        };
        return new PdfException(error, context is null ? text : $"{text} ({context})");
    }
}
