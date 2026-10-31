namespace NPMotors.Repository.Models.Enums
{
    public enum Status
    {
        Active,
        Inactive
    }

    public static class StatusExtensions
    {
        // The database stores status as lowercase text ('active' / 'inactive').
        public static string ToDbValue(this Status status) => status.ToString().ToLowerInvariant();
    }
}
