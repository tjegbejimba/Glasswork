namespace Glasswork.Core.Services;

public sealed class ResourceRevisionConflictException : InvalidOperationException
{
    public ResourceRevisionConflictException(string message) : base(message)
    {
    }
}
