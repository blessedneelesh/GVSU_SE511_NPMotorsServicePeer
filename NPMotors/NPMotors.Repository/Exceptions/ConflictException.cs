namespace NPMotors.Repository.Exceptions
{
    // A rule violated by the current state of the data (e.g. the vehicle was sold in the meantime); maps to 409.
    public class ConflictException : Exception
    {
        public ConflictException(string message) : base(message)
        {
        }
    }
}
