﻿using BuildingBlocks.EFCore;
using Microsoft.EntityFrameworkCore;

namespace BuildingBlocks.PersistMessageProcessor.Data;

using System.Net;
using Configurations;
using Core.Model;
using global::Polly;
using Microsoft.Extensions.Logging;

public class PersistMessageDbContext : DbContext, IPersistMessageDbContext
{
    public PersistMessageDbContext(DbContextOptions<PersistMessageDbContext> options)
        : base(options)
    {
    }

    public DbSet<PersistMessage> PersistMessages { get; set; }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.ApplyConfiguration(new PersistMessageConfiguration());
        base.OnModelCreating(builder);
        builder.ToSnakeCaseTables();
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        OnBeforeSaving();

        var policy = Policy.Handle<DbUpdateConcurrencyException>()
            .WaitAndRetryAsync(retryCount: 3,
                sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(1),
                onRetry: (exception, timeSpan, retryCount, context) =>
                {
                    if (exception != null)
                    {
                        var factory = LoggerFactory.Create(b => b.AddConsole());
                        var logger = factory.CreateLogger<PersistMessageDbContext>();

                        logger.LogError(exception,
                            "Request failed with {StatusCode}. Waiting {TimeSpan} before next retry. Retry attempt {RetryCount}.",
                            HttpStatusCode.Conflict,
                            timeSpan,
                            retryCount);
                    }
                });
        try
        {
            await policy.ExecuteAsync(async () => await base.SaveChangesAsync(cancellationToken));
        }
        catch (DbUpdateConcurrencyException ex)
        {
            foreach (var entry in ex.Entries)
            {
                var currentEntity = entry.Entity;
                var databaseValues = await entry.GetDatabaseValuesAsync(cancellationToken);

                if (databaseValues != null)
                {
                    entry.OriginalValues.SetValues(databaseValues);
                }
            }

            return await base.SaveChangesAsync(cancellationToken);
        }

        return 0;
    }

    private void OnBeforeSaving()
    {
        foreach (var entry in ChangeTracker.Entries<IVersion>())
        {
            switch (entry.State)
            {
                case EntityState.Modified:
                    entry.Entity.Version++;
                    break;

                case EntityState.Deleted:
                    entry.Entity.Version++;
                    break;
            }
        }
    }
}
