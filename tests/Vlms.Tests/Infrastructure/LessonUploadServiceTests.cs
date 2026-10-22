using Microsoft.Extensions.Options;
using Vlms.Domain;
using Vlms.Infrastructure.Curriculum;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>Upload validation for lesson proposals (FR-007, TC-017): size, type, content, caller.</summary>
public sealed class LessonUploadServiceTests
{
    private static readonly byte[] PdfBytes = [0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x34];
    private static readonly byte[] ZipBytes = [0x50, 0x4B, 0x03, 0x04, 0x00];

    private readonly InMemoryBlobStorage _blobs = new();

    private LessonUploadService Create(long maxBytes = 1024, int? userId = 10, params Role[] roles) =>
        new(_blobs, new FakeCurrentUserContext(userId, roles.Length == 0 ? [Role.Teacher] : roles),
            Options.Create(new LessonUploadOptions { MaxFileBytes = maxBytes }));

    [Fact]
    public async Task ValidPdf_IsStoredUnderTheProposersPendingPrefix()
    {
        var file = await Create().UploadAsync("Handout.pdf", "application/pdf", new MemoryStream(PdfBytes));

        Assert.Equal("Handout.pdf", file.FileName);
        Assert.StartsWith("lessons/pending/10/", file.BlobKey);
        Assert.True(_blobs.TryGetBlob(file.BlobKey, out var stored));
        Assert.Equal(PdfBytes, stored);
    }

    [Fact]
    public async Task FileOverTheLimit_IsRefused_AndNothingIsStored()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Create(maxBytes: 8).UploadAsync("Big.pdf", "application/pdf", new MemoryStream([.. PdfBytes, 1])));

        Assert.Contains("larger than", ex.Message);
        Assert.False(_blobs.TryGetBlob("anything", out _));
    }

    [Fact]
    public async Task FileExactlyAtTheLimit_IsAccepted()
    {
        var file = await Create(maxBytes: PdfBytes.Length).UploadAsync("Ok.pdf", "application/pdf", new MemoryStream(PdfBytes));

        Assert.True(_blobs.TryGetBlob(file.BlobKey, out _));
    }

    [Theory]
    [InlineData("script.exe")]
    [InlineData("page.svg")]
    [InlineData("archive.zip")]
    [InlineData("noextension")]
    [InlineData("double.pdf.exe")]
    public async Task DisallowedExtension_IsRefused(string name)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Create().UploadAsync(name, null, new MemoryStream(PdfBytes)));
    }

    [Fact]
    public async Task DeclaredContentTypeThatContradictsTheExtension_IsRefused()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Create().UploadAsync("Handout.pdf", "text/html", new MemoryStream(PdfBytes)));
    }

    [Fact]
    public async Task ContentThatIsNotTheDeclaredFormat_IsRefused()
    {
        // An executable renamed to .pdf.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Create().UploadAsync("Fake.pdf", "application/pdf", new MemoryStream([0x4D, 0x5A, 0x90, 0x00])));
    }

    [Fact]
    public async Task EmptyFile_IsRefused()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Create().UploadAsync("Empty.pdf", "application/pdf", new MemoryStream([])));
    }

    [Theory]
    [InlineData("Lesson.docx")]
    [InlineData("Slides.PPTX")]
    [InlineData("Marks.xlsx")]
    public async Task OfficeOpenXml_IsAccepted_WithAnEmptyOrOctetStreamContentType(string name)
    {
        var file = await Create().UploadAsync(name, "application/octet-stream", new MemoryStream(ZipBytes));

        Assert.Equal(name, file.FileName);
    }

    [Fact]
    public async Task TextFileWithBinaryContent_IsRefused()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Create().UploadAsync("Notes.txt", "text/plain", new MemoryStream([0x41, 0x00, 0x42])));
    }

    [Fact]
    public async Task ClientSuppliedDirectoryPart_IsStrippedFromTheName_AndNeverReachesTheKey()
    {
        var file = await Create().UploadAsync(@"..\..\evil\Handout.pdf", "application/pdf", new MemoryStream(PdfBytes));

        Assert.Equal("Handout.pdf", file.FileName);
        Assert.DoesNotContain("..", file.BlobKey);
        Assert.Equal(3, file.BlobKey.Count(c => c == '/') - 1); // lessons/pending/{user}/{guid}/{name}
    }

    [Theory]
    [InlineData(Role.Student)]
    [InlineData(Role.Parent)]
    [InlineData(Role.Approver)]
    public async Task NonTeacher_IsRefused(Role role)
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => Create(roles: role).UploadAsync("Handout.pdf", "application/pdf", new MemoryStream(PdfBytes)));
    }

    [Fact]
    public void DefaultLimit_Is25Megabytes() =>
        Assert.Equal(25L * 1024 * 1024, new LessonUploadOptions().MaxFileBytes);
}
