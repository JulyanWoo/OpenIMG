using Microsoft.EntityFrameworkCore;
using OpenIMG.Application.Abstractions.Persistence;
using OpenIMG.Core.Entities;

namespace OpenIMG.Infrastructure.Persistence;

public class ProjectRepository : IProjectRepository
{
    private readonly IDbContextFactory<OpenImgDbContext> _contextFactory;

    public ProjectRepository(IDbContextFactory<OpenImgDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<Project?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Projects
            .Include(p => p.ImageAssets)
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
    }

    public async Task<IReadOnlyList<Project>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var projects = await context.Projects.ToListAsync(cancellationToken);
        return projects.OrderByDescending(p => p.UpdatedAt).ToList();
    }

    public async Task<Project> CreateAsync(Project project, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        context.Projects.Add(project);
        await context.SaveChangesAsync(cancellationToken);
        return project;
    }

    public async Task UpdateAsync(Project project, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        project.UpdatedAt = DateTimeOffset.UtcNow;
        var persisted = await context.Projects.FindAsync([project.Id], cancellationToken)
            ?? throw new InvalidOperationException($"Project {project.Id} no longer exists.");
        context.Entry(persisted).CurrentValues.SetValues(project);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var project = await context.Projects.FindAsync(new object[] { id }, cancellationToken);
        if (project != null)
        {
            context.Projects.Remove(project);
            await context.SaveChangesAsync(cancellationToken);
        }
    }
}
