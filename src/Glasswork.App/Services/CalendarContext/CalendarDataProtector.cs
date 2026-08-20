namespace Glasswork.Services.CalendarContext;

public interface ICalendarDataProtector
{
    byte[] Protect(byte[] plaintext, byte[] entropy);

    byte[] Unprotect(byte[] protectedPayload, byte[] entropy);
}
