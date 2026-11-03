using System.Collections.Concurrent;

namespace SagaFlow.Returns;

public sealed record ReturnItem(string ProductId, int Quantity);
public sealed record CreateReturnRequest(Guid OrderId, string CustomerId, decimal Amount,
    IReadOnlyList<ReturnItem> Items, string? FailureAt = null);
public sealed record ReturnCase(Guid ReturnId, Guid OrderId, string CustomerId, decimal Amount,
    IReadOnlyList<ReturnItem> Items, string? FailureAt, DateTimeOffset CreatedAt, Guid SagaInstanceId);
public sealed record ReturnCreated(Guid ReturnId, Guid OrderId, Guid SagaInstanceId);

public sealed class ReturnRepository
{
    private readonly ConcurrentDictionary<Guid, ReturnCase> _cases = new();
    public ReturnCase? Find(Guid id) => _cases.GetValueOrDefault(id);
    public void Add(ReturnCase value)
    {
        if (!_cases.TryAdd(value.ReturnId, value)) throw new InvalidOperationException("Return already exists.");
    }
}
