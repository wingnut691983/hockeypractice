using HockeyPractice.Models;
using Microsoft.EntityFrameworkCore;

namespace HockeyPractice.Persistence;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Team> Teams => Set<Team>();
    public DbSet<Player> Players => Set<Player>();
    public DbSet<PracticePlan> Plans => Set<PracticePlan>();
    public DbSet<PlanLink> PlanLinks => Set<PlanLink>();
    public DbSet<PlanView> PlanViews => Set<PlanView>();
    public DbSet<Subscriber> Subscribers => Set<Subscriber>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Team>().HasIndex(t => t.Slug).IsUnique();

        b.Entity<Player>()
            .HasOne(p => p.Team).WithMany(t => t.Players)
            .HasForeignKey(p => p.TeamId).OnDelete(DeleteBehavior.Cascade);

        b.Entity<PracticePlan>()
            .HasOne(p => p.Team).WithMany(t => t.Plans)
            .HasForeignKey(p => p.TeamId).OnDelete(DeleteBehavior.Cascade);

        // Listing a team's plans always sorts by practice date.
        b.Entity<PracticePlan>().HasIndex(p => new { p.TeamId, p.PracticeDateLocal });

        b.Entity<PlanLink>()
            .HasOne(l => l.PracticePlan).WithMany(p => p.Links)
            .HasForeignKey(l => l.PracticePlanId).OnDelete(DeleteBehavior.Cascade);

        b.Entity<PlanView>()
            .HasOne(v => v.PracticePlan).WithMany(p => p.Views)
            .HasForeignKey(v => v.PracticePlanId).OnDelete(DeleteBehavior.Cascade);

        // A player row is removed when they leave the roster; their view history goes with the
        // player reference but the count of anonymous views stays intact.
        b.Entity<PlanView>()
            .HasOne(v => v.Player).WithMany()
            .HasForeignKey(v => v.PlayerId).OnDelete(DeleteBehavior.SetNull);

        // Two separate uniqueness rules, not one. A shared family device is legitimately used
        // by more than one player over a season — keying uniqueness purely on ViewerKey meant
        // a second player picking themselves on the same phone their sibling already used could
        // never be recorded; the row was permanently claimed by whoever viewed first. Once a
        // player is identified, THEY are the identity that must be unique per plan, independent
        // of which device did the viewing. Only while anonymous (no player chosen — a parent who
        // skipped, most likely) does the device itself stand in as the identity.
        b.Entity<PlanView>()
            .HasIndex(v => new { v.PracticePlanId, v.PlayerId })
            .IsUnique()
            .HasFilter("[PlayerId] IS NOT NULL");

        b.Entity<PlanView>()
            .HasIndex(v => new { v.PracticePlanId, v.ViewerKey })
            .IsUnique()
            .HasFilter("[PlayerId] IS NULL");

        b.Entity<Subscriber>()
            .HasOne(s => s.Team).WithMany(t => t.Subscribers)
            .HasForeignKey(s => s.TeamId).OnDelete(DeleteBehavior.Cascade);

        b.Entity<Subscriber>()
            .HasOne(s => s.Player).WithMany()
            .HasForeignKey(s => s.PlayerId).OnDelete(DeleteBehavior.SetNull);

        b.Entity<Subscriber>().HasIndex(s => new { s.TeamId, s.Email }).IsUnique();
        b.Entity<Subscriber>().HasIndex(s => s.ConfirmToken);
        b.Entity<Subscriber>().HasIndex(s => s.UnsubToken);
    }
}
