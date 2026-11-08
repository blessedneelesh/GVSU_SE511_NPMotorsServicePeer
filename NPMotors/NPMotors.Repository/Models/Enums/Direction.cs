namespace NPMotors.Repository.Models.Enums
{
    public enum Direction
    {
        In,
        Out
    }

    public static class DirectionExtensions
    {
        // The database stores direction as the direction_enum values 'in' / 'out'.
        public static string ToDbValue(this Direction direction) => direction.ToString().ToLowerInvariant();
    }
}
