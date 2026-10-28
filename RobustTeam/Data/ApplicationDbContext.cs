
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using AiUseCaseDiscovery.Models;

namespace AiUseCaseDiscovery.Data;

public class ApplicationDbContext : IdentityDbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options) { }

    public DbSet<UserAiSettings> UserAiSettings => Set<UserAiSettings>();
    public DbSet<LogProcessingJob> LogProcessingJobs => Set<LogProcessingJob>();
}
