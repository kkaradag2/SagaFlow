using Sample.DomainStarters.Return.Contracts;

namespace Sample.DomainStarters.Return.Services;

public interface IReturnService
{
    Task<CreateReturnResponse> CreateAsync(CreateReturnRequest request, CancellationToken cancellationToken);
    Task<ReturnStatusResponse?> FindAsync(Guid returnId, CancellationToken cancellationToken);
}
