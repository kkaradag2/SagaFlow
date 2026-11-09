using SagaFlow.Core.State;
using SagaFlow.Storage.PostgreSQL.Persistence;

namespace SagaFlow.Storage.PostgreSQL.Models;

/// <summary>Stores the saga aggregate together with its relational identity and concurrency token.</summary>
internal sealed class SagaRow
{
    public string Id { get; init; } = null!;
    public long Revision { get; private set; }
    public string Document { get; private set; } = null!;

    public SagaInstance Read() => StorageJson.Read<SagaInstance>(Document);

    public void Write(SagaInstance state)
    {
        state.Revision = checked(Revision + 1);
        Document = StorageJson.Write(state);
        Revision = state.Revision;
    }
}
