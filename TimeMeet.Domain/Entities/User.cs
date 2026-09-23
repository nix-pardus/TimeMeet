namespace TimeMeet.Domain.Entities;

public sealed class User
{
    public Guid Id { get; set; }
    public required string Email { get; set; }
    public required string DisplayName { get; set; }
    public required string TimeZone { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public bool IsDonor { get; set; }
    public ICollection<Meeting> Meetings { get; set; } = []; 
    public ICollection<Participant> Participations { get; set; } = [];
}
