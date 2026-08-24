using System.Security.Cryptography;

namespace Glasswork.Services.CalendarContext;

public sealed class DpapiCalendarDataProtector : ICalendarDataProtector
{
    public byte[] Protect(byte[] plaintext, byte[] entropy) =>
        ProtectedData.Protect(plaintext, entropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] protectedPayload, byte[] entropy) =>
        ProtectedData.Unprotect(protectedPayload, entropy, DataProtectionScope.CurrentUser);
}
