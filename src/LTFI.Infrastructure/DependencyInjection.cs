using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using LTFI.Core.Abstractions;
using LTFI.Infrastructure.Llm;
using LTFI.Infrastructure.Persistence;
using LTFI.Infrastructure.Services;

namespace LTFI.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the SQLite-backed persistence layer and the application services.
    /// A <see cref="IDbContextFactory{TContext}"/> is used so each operation gets its own
    /// short-lived context — the right model for a desktop app with no request scope.
    /// </summary>
    public static IServiceCollection AddLtfiInfrastructure(this IServiceCollection services)
    {
        services.AddDbContextFactory<LtfiDbContext>(options =>
            options.UseSqlite(DbPaths.GetConnectionString()));

        services.AddSingleton<IProjectService, ProjectService>();
        services.AddSingleton<ITaskService, TaskService>();
        services.AddSingleton<IMilestoneService, MilestoneService>();
        // Holds the live focus timer in memory, so it must be a singleton.
        services.AddSingleton<IFocusSessionService, FocusSessionService>();
        services.AddSingleton<IInsightsService, InsightsService>();
        services.AddSingleton<IReviewService, ReviewService>();
        services.AddSingleton<IEvidenceService, EvidenceService>();

        // Optional LLM coach (plan §5.3). Registered unconditionally: with no key the provider just
        // reports IsConfigured=false, so the app always starts and the UI shows a setup hint.
        services.AddSingleton(_ => LlmSettings.Load(Path.Combine(DbPaths.AppDataDirectory, LlmSettings.FileName)));
        services.AddSingleton<IApiKeyStore>(_ =>
            new DpapiApiKeyStore(Path.Combine(DbPaths.AppDataDirectory, DpapiApiKeyStore.FileName)));
        services.AddSingleton<ILlmProvider>(sp =>
            new OpenAiProvider(new HttpClient(), sp.GetRequiredService<LlmSettings>(), sp.GetRequiredService<IApiKeyStore>()));
        services.AddSingleton<ICoachService, CoachService>();

        return services;
    }

    /// <summary>Applies any pending EF Core migrations, creating the database if needed.</summary>
    public static void MigrateLtfiDatabase(this IServiceProvider services)
    {
        var factory = services.GetRequiredService<IDbContextFactory<LtfiDbContext>>();
        using var db = factory.CreateDbContext();
        db.Database.Migrate();
    }
}
