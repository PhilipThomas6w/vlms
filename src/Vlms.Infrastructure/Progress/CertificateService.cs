using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Vlms.Domain;

namespace Vlms.Infrastructure.Progress;

/// <summary>
/// Certificate generation (docs/design/low-level-design.md "CertificateService", STATE.md):
/// generates a PDF via QuestPDF from a minimal template, uploads it to blob storage (via
/// <see cref="IBlobStorage"/> — adr/0001-technology-stack.md: Azure Blob Storage; the real
/// implementation is <c>Storage/AzureBlobStorage.cs</c>, tests inject an in-memory fake), and
/// writes a <see cref="Certificate"/> row. data-design.md: "Auto-generated on completion" — one
/// certificate per <see cref="StudentLessonCompletion"/> (1:1), not just on promotion.
///
/// Authorization: none of its own, same reasoning as <see cref="PromotionService"/> — triggered
/// only by the already role-checked <see cref="CompletionService.MarkCompleteAsync"/>.
/// </summary>
public sealed class CertificateService
{
    private readonly VlmsDbContext _db;
    private readonly IBlobStorage _blobStorage;

    static CertificateService()
    {
        // QuestPDF Community licence (adr/0001-technology-stack.md) — must be set once before any
        // document is generated; harmless to set repeatedly, so a static constructor (runs once
        // per process) is the simplest place, independent of how/whether Vlms.Web's Program.cs
        // ever calls this class.
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public CertificateService(VlmsDbContext db, IBlobStorage blobStorage)
    {
        _db = db;
        _blobStorage = blobStorage;
    }

    public async Task<Certificate> GenerateAsync(int completionId, CancellationToken ct = default)
    {
        var completion = await _db.StudentLessonCompletions
            .Include(c => c.Student)
            .Include(c => c.Lesson)
            .SingleAsync(c => c.Id == completionId, ct);

        var student = completion.Student
            ?? throw new InvalidOperationException($"Completion {completionId}'s Student failed to load.");
        var lesson = completion.Lesson
            ?? throw new InvalidOperationException($"Completion {completionId}'s Lesson failed to load.");

        var pdfBytes = BuildCertificatePdf(student.Name, lesson.Title, completion.CompletedAt);

        var blobKey = $"certificates/{completion.StudentId}/{completion.Id}.pdf";
        await _blobStorage.UploadAsync(blobKey, pdfBytes, ct);

        var certificate = new Certificate
        {
            Id = await NextId(ct),
            StudentLessonCompletionId = completion.Id,
            GeneratedAt = DateTime.UtcNow,
            BlobKey = blobKey
        };

        _db.Certificates.Add(certificate);
        await _db.SaveChangesAsync(ct);
        return certificate;
    }

    private static byte[] BuildCertificatePdf(string studentName, string lessonTitle, DateTime completedAt)
    {
        using var stream = new MemoryStream();
        Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(2, Unit.Centimetre);
                page.Content().Column(column =>
                {
                    column.Item().AlignCenter().Text("Certificate of Completion").FontSize(24).Bold();
                    column.Item().PaddingTop(20).AlignCenter().Text(studentName).FontSize(18).Bold();
                    column.Item().PaddingTop(8).AlignCenter().Text($"has completed \"{lessonTitle}\"").FontSize(14);
                    column.Item().PaddingTop(8).AlignCenter().Text($"Awarded {completedAt:d MMMM yyyy}").FontSize(12);
                });
            });
        }).GeneratePdf(stream);

        return stream.ToArray();
    }

    // Same application-assigned-id pattern as LessonProposalService.NextId — see its comment.
    private async Task<int> NextId(CancellationToken ct) =>
        (await _db.Certificates.Select(x => (int?)x.Id).MaxAsync(ct) ?? 0) + 1;
}
