using Microsoft.EntityFrameworkCore;

namespace MapStitcher.Database
{
    public class ApplicationDbContext : DbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options) { }

        public DbSet<CadGridSession> CadGridSessions => Set<CadGridSession>();
        public DbSet<CadGridSheet> CadGridSheets => Set<CadGridSheet>();
        public DbSet<CadGridMissingSlot> CadGridMissingSlots => Set<CadGridMissingSlot>();
        public DbSet<CadGridMergeError> CadGridMergeErrors => Set<CadGridMergeError>();
        public DbSet<CadGridSkippedFile> CadGridSkippedFiles => Set<CadGridSkippedFile>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<CadGridSession>(e =>
            {
                e.ToTable("CadGridSessions");
                e.HasKey(s => s.SessionId);
                e.Property(s => s.SessionId).HasMaxLength(32).IsFixedLength();
                e.Property(s => s.MergeOutputFileName).HasMaxLength(260);
            });

            modelBuilder.Entity<CadGridSheet>(e =>
            {
                e.ToTable("CadGridSheets");
                e.HasKey(s => s.SheetID);
                e.Property(s => s.SessionId).HasMaxLength(32).IsFixedLength().IsRequired();
                e.Property(s => s.FileName).IsRequired().HasMaxLength(260);
                e.Property(s => s.FilePath).IsRequired().HasMaxLength(500);
                e.Property(s => s.SheetNumber).IsRequired().HasMaxLength(50);
                e.HasIndex(s => s.SessionId);
                e.HasIndex(s => new { s.SessionId, s.Row, s.Column });

                e.HasOne(s => s.Session)
                 .WithMany(ss => ss.Sheets)
                 .HasForeignKey(s => s.SessionId)
                 .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<CadGridMissingSlot>(e =>
            {
                e.ToTable("CadGridMissingSlots");
                e.HasKey(s => s.MissingSlotID);
                e.Property(s => s.SessionId).HasMaxLength(32).IsFixedLength().IsRequired();
                e.Property(s => s.SheetNumber).IsRequired().HasMaxLength(50);
                e.HasIndex(s => s.SessionId);

                e.HasOne(s => s.Session)
                 .WithMany(ss => ss.MissingSlots)
                 .HasForeignKey(s => s.SessionId)
                 .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<CadGridMergeError>(e =>
            {
                e.ToTable("CadGridMergeErrors");
                e.HasKey(s => s.MergeErrorID);
                e.Property(s => s.SessionId).HasMaxLength(32).IsFixedLength().IsRequired();
                e.Property(s => s.ErrorMessage).IsRequired().HasMaxLength(1000);
                e.HasIndex(s => s.SessionId);

                e.HasOne(s => s.Session)
                 .WithMany(ss => ss.MergeErrors)
                 .HasForeignKey(s => s.SessionId)
                 .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<CadGridSkippedFile>(e =>
            {
                e.ToTable("CadGridSkippedFiles");
                e.HasKey(s => s.SkippedFileID);
                e.Property(s => s.SessionId).HasMaxLength(32).IsFixedLength().IsRequired();
                e.Property(s => s.FileName).IsRequired().HasMaxLength(260);
                e.HasIndex(s => s.SessionId);

                e.HasOne(s => s.Session)
                 .WithMany(ss => ss.SkippedFiles)
                 .HasForeignKey(s => s.SessionId)
                 .OnDelete(DeleteBehavior.Cascade);
            });
        }
    }
}
