using System.IO.Compression;
using System.Text;

namespace Vlms.Tests.Infrastructure;

/// <summary>Small files of each handout type (docs/adr/0016), distinct by <c>marker</c>, for the handout tests.</summary>
internal static class HandoutBytes
{
    public static byte[] Pdf(string marker = "a") => Encoding.ASCII.GetBytes($"%PDF-1.4\n% {marker}\n%%EOF\n");

    public static byte[] Png(string marker = "a") =>
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. Encoding.ASCII.GetBytes(marker)];

    public static byte[] Jpeg(string marker = "a") => [0xFF, 0xD8, 0xFF, 0xE0, .. Encoding.ASCII.GetBytes(marker)];

    public static byte[] Docx(string marker = "a")
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, "[Content_Types].xml", "<Types/>");
            Write(zip, "word/document.xml", $"<document>{marker}</document>");
        }
        return buffer.ToArray();
    }

    /// <summary>A zip that is not a Word document (no word/document.xml).</summary>
    public static byte[] Zip()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, "readme.txt", "hello");
        }
        return buffer.ToArray();
    }

    private static void Write(ZipArchive zip, string name, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open());
        writer.Write(text);
    }
}
