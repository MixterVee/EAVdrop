namespace EAVdrop.Models;

public sealed class ActivityLogResultDto
{
    public List<ActivityLogEntryDto> Items { get; set; } = [];
    public int TotalRecordCount { get; set; }
}

public sealed class ActivityLogEntryDto
{
    public long Id { get; set; }
    public string? Name { get; set; }
    public string? Overview { get; set; }
    public string? ShortOverview { get; set; }
    public string? Type { get; set; }
    public string? ItemId { get; set; }
    public DateTimeOffset Date { get; set; }
    public string? UserId { get; set; }
    public string? Severity { get; set; }

    public string UserName { get; set; } = "";
    public string DateDisplay => Date.LocalDateTime.ToString("g");
    public string Detail => !string.IsNullOrWhiteSpace(Overview) ? Overview! : ShortOverview ?? Type ?? "Activity";
}
