namespace NPMotors.Repository.Models.Enums
{
    public enum VehicleStatus
    {
        Available,
        Sold,
        Reserved
    }

    public static class VehicleStatusExtensions
    {
        // The database stores vehicle status as lowercase text ('available' / 'sold' / 'reserved').
        public static string ToDbValue(this VehicleStatus status) => status.ToString().ToLowerInvariant();
    }
}
