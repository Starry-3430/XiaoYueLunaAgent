using Microsoft.EntityFrameworkCore;
using Luna.Models;

namespace Luna.Data;

public class LunaDbContext : DbContext
{
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<TurnSummary> TurnSummaries => Set<TurnSummary>();
    public DbSet<DiaryEntry> DiaryEntries => Set<DiaryEntry>();
    public DbSet<DiaryDay> DiaryDays => Set<DiaryDay>();
    public DbSet<Attachment> Attachments => Set<Attachment>();

    public LunaDbContext(DbContextOptions<LunaDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Session>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnType("TEXT");
            entity.Property(e => e.Title).HasColumnType("TEXT");
            entity.Property(e => e.CreatedAtUtc).HasColumnType("TEXT");
            entity.Property(e => e.UpdatedAtUtc).HasColumnType("TEXT");
            entity.Property(e => e.IsArchived).HasColumnType("INTEGER").HasDefaultValue(0);
            entity.HasIndex(e => e.UpdatedAtUtc, "IX_Sessions_UpdatedAtUtc");
        });

        modelBuilder.Entity<Message>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.SessionId).HasColumnType("TEXT").IsRequired();
            entity.Property(e => e.TurnId).HasColumnType("TEXT").IsRequired();
            entity.Property(e => e.Role).HasColumnType("TEXT").IsRequired();
            entity.Property(e => e.Content).HasColumnType("TEXT").IsRequired();
            entity.Property(e => e.ContentType).HasColumnType("TEXT").IsRequired().HasDefaultValue("text");
            entity.Property(e => e.CreatedAtUtc).HasColumnType("TEXT").IsRequired();
            entity.Property(e => e.LogicalDate).HasColumnType("TEXT").IsRequired();
            entity.HasIndex(["TurnId", "SessionId", "LogicalDate"], "IX_Messages_TurnId_SessionId_LogicalDate");
        });

        modelBuilder.Entity<TurnSummary>(entity =>
        {
            entity.HasKey(e => e.TurnId);
            entity.Property(e => e.TurnId).HasColumnType("TEXT");
            entity.Property(e => e.SessionId).HasColumnType("TEXT").IsRequired();
            entity.Property(e => e.TurnIndex).HasColumnType("INTEGER").IsRequired();
            entity.Property(e => e.LogicalDate).HasColumnType("TEXT").IsRequired();
            entity.Property(e => e.SummaryJson).HasColumnType("TEXT").IsRequired();
            entity.Property(e => e.Model).HasColumnType("TEXT");
            entity.Property(e => e.TokenCount).HasColumnType("INTEGER");
            entity.Property(e => e.CreatedAtUtc).HasColumnType("TEXT").IsRequired();
            entity.HasIndex(["LogicalDate", "SessionId"], "IX_TurnSummaries_LogicalDate_SessionId");
        });

        modelBuilder.Entity<DiaryEntry>(entity =>
        {
            entity.HasKey(e => e.LogicalDate);
            entity.Property(e => e.LogicalDate).HasColumnType("TEXT");
            entity.Property(e => e.Content).HasColumnType("TEXT").IsRequired();
            entity.Property(e => e.Model).HasColumnType("TEXT");
            entity.Property(e => e.SourceSessionIds).HasColumnType("TEXT");
            entity.Property(e => e.SourceTurnIds).HasColumnType("TEXT");
            entity.Property(e => e.TokenCount).HasColumnType("INTEGER");
            entity.Property(e => e.CreatedAtUtc).HasColumnType("TEXT").IsRequired();
            entity.Property(e => e.UpdatedAtUtc).HasColumnType("TEXT").IsRequired();
        });

        modelBuilder.Entity<DiaryDay>(entity =>
        {
            entity.HasKey(e => e.LogicalDate);
            entity.Property(e => e.LogicalDate).HasColumnType("TEXT");
            entity.Property(e => e.Status).HasColumnType("TEXT").IsRequired().HasDefaultValue("Pending");
            entity.Property(e => e.LastError).HasColumnType("TEXT");
            entity.Property(e => e.UpdatedAtUtc).HasColumnType("TEXT").IsRequired();
        });

        modelBuilder.Entity<Attachment>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnType("TEXT");
            entity.Property(e => e.MessageId).HasColumnType("INTEGER").IsRequired();
            entity.Property(e => e.FileName).HasColumnType("TEXT").IsRequired();
            entity.Property(e => e.MimeType).HasColumnType("TEXT");
            entity.Property(e => e.SizeBytes).HasColumnType("INTEGER");
            entity.Property(e => e.Sha256).HasColumnType("TEXT");
            entity.Property(e => e.StoredPath).HasColumnType("TEXT");
            entity.Property(e => e.CreatedAtUtc).HasColumnType("TEXT").IsRequired();
            entity.HasIndex(e => e.MessageId, "IX_Attachments_MessageId");
        });
    }

    public void InitializeFts()
    {
        Database.ExecuteSqlRaw(
            """CREATE VIRTUAL TABLE IF NOT EXISTS DiaryFts USING fts5(LogicalDate UNINDEXED, Content, tokenize='trigram')""");

        Database.ExecuteSqlRaw(
            """CREATE TRIGGER IF NOT EXISTS DiaryEntries_ai AFTER INSERT ON DiaryEntries BEGIN INSERT INTO DiaryFts(LogicalDate, Content) VALUES (new.LogicalDate, new.Content); END""");

        Database.ExecuteSqlRaw(
            """CREATE TRIGGER IF NOT EXISTS DiaryEntries_ad AFTER DELETE ON DiaryEntries BEGIN INSERT INTO DiaryFts(DiaryFts, LogicalDate, Content) VALUES('delete', old.LogicalDate, old.Content); END""");

        Database.ExecuteSqlRaw(
            """CREATE TRIGGER IF NOT EXISTS DiaryEntries_au AFTER UPDATE ON DiaryEntries BEGIN INSERT INTO DiaryFts(DiaryFts, LogicalDate, Content) VALUES('delete', old.LogicalDate, old.Content); INSERT INTO DiaryFts(LogicalDate, Content) VALUES (new.LogicalDate, new.Content); END""");
    }
}