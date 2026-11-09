using Microsoft.EntityFrameworkCore;
using SagaFlow.Storage.PostgreSQL.Models;

namespace SagaFlow.Storage.PostgreSQL.Persistence;

/// <summary>Maps database rows and owns the row-locking queries used inside write transactions.</summary>
internal sealed class StorageContext(DbContextOptions<StorageContext> options) : DbContext(options)
{
    internal DbSet<SagaRow> Instances => Set<SagaRow>();
    internal DbSet<InvocationRow> Invocations => Set<InvocationRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var instances = modelBuilder.Entity<SagaRow>();
        instances.ToTable("instances", "sagaflow");
        instances.HasKey(row => row.Id);
        instances.Property(row => row.Id).HasColumnName("id");
        instances.Property(row => row.Revision).HasColumnName("revision").IsConcurrencyToken();
        instances.Property(row => row.Document).HasColumnName("document").HasColumnType("json");

        var invocations = modelBuilder.Entity<InvocationRow>();
        invocations.ToTable("incoming_invocations", "sagaflow");
        invocations.HasKey(row => row.Id);
        invocations.Property(row => row.Id).HasColumnName("id");
        invocations.Property(row => row.TargetId).HasColumnName("target_id");
        invocations.Property(row => row.Decision).HasColumnName("decision").HasColumnType("json");
        invocations.HasOne<SagaRow>().WithOne().HasForeignKey<InvocationRow>(row => row.TargetId);
    }

    internal async Task<SagaRow?> LockInstanceAsync(string id, CancellationToken cancellationToken)
    {
        var rows = await Instances.FromSql($"""
            SELECT id, revision, document FROM sagaflow.instances WHERE id = {id} FOR UPDATE
            """).ToListAsync(cancellationToken);
        return rows.SingleOrDefault();
    }

    internal async Task<InvocationRow> LockInvocationAsync(Guid id, CancellationToken cancellationToken)
    {
        // The unique key also serializes contenders when the invocation has no row yet.
        await Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO sagaflow.incoming_invocations (id) VALUES ({id}) ON CONFLICT (id) DO NOTHING
            """, cancellationToken);
        var rows = await Invocations.FromSql($"""
            SELECT id, target_id, decision FROM sagaflow.incoming_invocations WHERE id = {id} FOR UPDATE
            """).ToListAsync(cancellationToken);
        return rows.Single();
    }
}
