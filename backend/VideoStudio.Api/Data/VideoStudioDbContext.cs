using Microsoft.EntityFrameworkCore;
using VideoStudio.Api.Domain;

namespace VideoStudio.Api.Data;

public sealed class VideoStudioDbContext(DbContextOptions<VideoStudioDbContext> options) : DbContext(options)
{
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Character> Characters => Set<Character>();
    public DbSet<Scene> Scenes => Set<Scene>();
    public DbSet<Shot> Shots => Set<Shot>();
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<AudioTrack> AudioTracks => Set<AudioTrack>();
    public DbSet<DialogueLine> DialogueLines => Set<DialogueLine>();
    public DbSet<RenderJob> RenderJobs => Set<RenderJob>();
    public DbSet<FinalVideo> FinalVideos => Set<FinalVideo>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Project>().HasMany(p => p.Scenes).WithOne(s => s.Project).HasForeignKey(s => s.ProjectId);
        modelBuilder.Entity<Project>().HasMany(p => p.Characters).WithOne(c => c.Project).HasForeignKey(c => c.ProjectId);
        modelBuilder.Entity<Project>().HasMany(p => p.Assets).WithOne(a => a.Project).HasForeignKey(a => a.ProjectId);
        modelBuilder.Entity<Project>().HasMany(p => p.AudioTracks).WithOne(a => a.Project).HasForeignKey(a => a.ProjectId);
        modelBuilder.Entity<Project>().HasMany(p => p.DialogueLines).WithOne(d => d.Project).HasForeignKey(d => d.ProjectId);
        modelBuilder.Entity<Project>().HasMany(p => p.RenderJobs).WithOne(j => j.Project).HasForeignKey(j => j.ProjectId);
        modelBuilder.Entity<Project>().HasOne(p => p.FinalVideo).WithOne(v => v.Project).HasForeignKey<FinalVideo>(v => v.ProjectId);

        modelBuilder.Entity<Character>().HasOne(c => c.ReferenceAsset).WithMany().HasForeignKey(c => c.ReferenceAssetId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<Asset>().HasOne(a => a.Character).WithMany().HasForeignKey(a => a.CharacterId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<Asset>().HasOne(a => a.Shot).WithMany().HasForeignKey(a => a.ShotId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<Scene>().HasMany(s => s.Shots).WithOne(s => s.Scene).HasForeignKey(s => s.SceneId);
        modelBuilder.Entity<DialogueLine>().HasOne(d => d.Scene).WithMany(s => s.DialogueLines).HasForeignKey(d => d.SceneId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<Shot>().HasOne(s => s.Project).WithMany().HasForeignKey(s => s.ProjectId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<Shot>().HasOne(s => s.StartImageAsset).WithMany().HasForeignKey(s => s.StartImageAssetId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<Shot>().HasMany(s => s.RenderJobs).WithOne(j => j.Shot).HasForeignKey(j => j.ShotId);
        modelBuilder.Entity<RenderJob>().HasOne(j => j.Character).WithMany().HasForeignKey(j => j.CharacterId).OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<RenderJob>().HasOne(j => j.DialogueLine).WithMany().HasForeignKey(j => j.DialogueLineId);

        modelBuilder.Entity<Project>().Property(p => p.Status).HasConversion<string>();
        modelBuilder.Entity<Project>().Property(p => p.QualityGoal).HasDefaultValue("Balanced");
        modelBuilder.Entity<Project>().Property(p => p.BeatSheetJson).HasDefaultValue("[]");
        modelBuilder.Entity<Project>().Property(p => p.ActBreakdownJson).HasDefaultValue("[]");
        modelBuilder.Entity<Project>().Property(p => p.CharacterBibleJson).HasDefaultValue("[]");
        modelBuilder.Entity<Project>().Property(p => p.LocationBibleJson).HasDefaultValue("[]");
        modelBuilder.Entity<Project>().Property(p => p.TimelineContinuityJson).HasDefaultValue("[]");
        modelBuilder.Entity<Project>().Property(p => p.VisualContinuityRulesJson).HasDefaultValue("[]");
        modelBuilder.Entity<Project>().Property(p => p.RenderStrategyRecommendationJson).HasDefaultValue("{}");
        modelBuilder.Entity<Character>().Property(c => c.CharacterBibleJson).HasDefaultValue("{}");
        modelBuilder.Entity<Shot>().Property(s => s.Status).HasConversion<string>();
        modelBuilder.Entity<Shot>().Property(s => s.GenerationMode).HasConversion<string>();
        modelBuilder.Entity<Shot>().Property(s => s.InvolvedCharacterIdsJson).HasDefaultValue("[]");
        modelBuilder.Entity<Shot>().Property(s => s.AssemblyExtensionAllowed).HasDefaultValue(true);
        modelBuilder.Entity<RenderJob>().Property(j => j.Status).HasConversion<string>();
        modelBuilder.Entity<RenderJob>().Property(j => j.JobType).HasConversion<string>();
        modelBuilder.Entity<RenderJob>().Property(j => j.Preset).HasConversion<string>();
        modelBuilder.Entity<RenderJob>().Property(j => j.RenderDurationMode).HasConversion<string>();
        modelBuilder.Entity<RenderJob>().Property(j => j.GenerationMode).HasConversion<string>();
        modelBuilder.Entity<Asset>().Property(a => a.Type).HasConversion<string>();

        modelBuilder.Entity<Scene>().HasIndex(s => new { s.ProjectId, s.Index });
        modelBuilder.Entity<Shot>().HasIndex(s => new { s.SceneId, s.Index });
        modelBuilder.Entity<DialogueLine>().HasIndex(d => new { d.ProjectId, d.SceneId, d.EstimatedStartSecond });
        modelBuilder.Entity<RenderJob>().HasIndex(j => new { j.ProjectId, j.Status });
    }
}
