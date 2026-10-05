using Microsoft.EntityFrameworkCore;
using TimeMeet.Domain.Entities;

namespace TimeMeet.Infrastructure.Data;

public sealed class TimeMeetDbContext(DbContextOptions<TimeMeetDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Meeting> Meetings => Set<Meeting>();
    public DbSet<GridCell> GridCells => Set<GridCell>();
    public DbSet<Participant> Participants => Set<Participant>();
    public DbSet<Availability> Availabilities => Set<Availability>();
    public DbSet<Invitation> Invitations => Set<Invitation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>().HasKey(x => x.Id);
        modelBuilder.Entity<User>().HasIndex(x => x.Email).IsUnique();
        modelBuilder.Entity<User>().Property(x => x.Email).HasMaxLength(320);
        modelBuilder.Entity<User>().Property(x => x.TimeZone).HasMaxLength(100);

        modelBuilder.Entity<Meeting>().HasKey(x => x.Id);
        modelBuilder.Entity<Meeting>().HasIndex(x => x.ShortCode).IsUnique();
        modelBuilder.Entity<Meeting>().HasIndex(x => x.OwnerToken).IsUnique();
        modelBuilder.Entity<Meeting>().HasQueryFilter(x => x.Status != Domain.Enums.MeetingStatus.Archived && x.Status != Domain.Enums.MeetingStatus.Deleted);
        modelBuilder.Entity<Meeting>().Property(x => x.ShortCode).HasMaxLength(10);
        modelBuilder.Entity<Meeting>().Property(x => x.OwnerToken).HasMaxLength(64);
        modelBuilder.Entity<Meeting>().Property(x => x.Title).HasMaxLength(200);
        modelBuilder.Entity<Meeting>().Property(x => x.TimeZone).HasMaxLength(100);
        modelBuilder.Entity<Meeting>().Property(x => x.DeletionReason).HasConversion<int>();
        modelBuilder.Entity<Meeting>().HasOne(x => x.Organizer).WithMany(x => x.Meetings).HasForeignKey(x => x.OrganizerId).OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<GridCell>().HasKey(x => x.Id);
        modelBuilder.Entity<GridCell>().HasIndex(x => x.MeetingId);
        modelBuilder.Entity<GridCell>().HasIndex(x => new { x.MeetingId, x.StartTime });
        modelBuilder.Entity<GridCell>().HasOne(x => x.Meeting).WithMany(x => x.GridCells).HasForeignKey(x => x.MeetingId).OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Participant>().HasKey(x => x.Id);
        modelBuilder.Entity<Participant>().HasIndex(x => new { x.MeetingId, x.ParticipantToken }).IsUnique();
        modelBuilder.Entity<Participant>().HasIndex(x => x.ParticipantToken);
        modelBuilder.Entity<Participant>().HasOne(x => x.Meeting).WithMany(x => x.Participants).HasForeignKey(x => x.MeetingId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Participant>().HasOne(x => x.User).WithMany(x => x.Participations).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<Availability>().HasKey(x => x.Id);
        modelBuilder.Entity<Availability>().HasIndex(x => x.ParticipantId);
        modelBuilder.Entity<Availability>().HasIndex(x => x.GridCellId);
        modelBuilder.Entity<Availability>().HasIndex(x => new { x.ParticipantId, x.GridCellId }).IsUnique();
        modelBuilder.Entity<Availability>().HasOne(x => x.Participant).WithMany(x => x.Availabilities).HasForeignKey(x => x.ParticipantId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Availability>().HasOne(x => x.GridCell).WithMany(x => x.Availabilities).HasForeignKey(x => x.GridCellId).OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Invitation>().HasKey(x => x.Id);
        modelBuilder.Entity<Invitation>().HasIndex(x => x.Token).IsUnique();
        modelBuilder.Entity<Invitation>().HasOne(x => x.Meeting).WithMany(x => x.Invitations).HasForeignKey(x => x.MeetingId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Invitation>().HasOne(x => x.Participant).WithMany(x => x.Invitations).HasForeignKey(x => x.ParticipantId).OnDelete(DeleteBehavior.SetNull);
    }
}
