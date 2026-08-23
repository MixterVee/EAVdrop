namespace EAVdrop.Models;

public sealed class UserQueryResultDto
{
    public List<UserDto> Items { get; set; } = [];
    public int TotalRecordCount { get; set; }
}

public sealed class UserDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "Unknown";
    public DateTimeOffset? LastLoginDate { get; set; }
    public DateTimeOffset? LastActivityDate { get; set; }
    public UserPolicyDto? Policy { get; set; }

    public string LastActivityDisplay => LastActivityDate.HasValue
        ? $"Last active {LastActivityDate.Value.LocalDateTime:g}"
        : "No recent activity date";
}

public sealed class UserPolicyDto
{
    public bool IsAdministrator { get; set; }
    public bool IsDisabled { get; set; }
    public bool IsHidden { get; set; }
}
