namespace Vlms.Domain;

/// <summary>
/// An entity that belongs to one <see cref="Chapter"/> (docs/adr/0010-chapters.md). Every such
/// entity carries its own <see cref="ChapterId"/> (denormalised onto children such as completions
/// and consent details, so each query filter is a plain column comparison and no filter has to
/// join back up the tree), and <c>VlmsDbContext</c> gives every one of them the "Chapter" query
/// filter. <c>VlmsDbContext.SaveChanges</c> fills in a child's chapter from its parent row, and
/// refuses a child whose chapter differs from its parent's or any later change of chapter.
/// </summary>
public interface IChapterOwned
{
    int ChapterId { get; }
}
