namespace Vlms.Domain;

public sealed class Rank
{
    public required int Id { get; init; }
    public required int Order { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }

    public bool IsBefore(Rank other) => Order < other.Order;
}
