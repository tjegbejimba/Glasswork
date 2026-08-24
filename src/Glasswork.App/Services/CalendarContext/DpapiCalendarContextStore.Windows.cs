using System;
using System.IO;

namespace Glasswork.Services.CalendarContext;

public sealed partial class DpapiCalendarContextStore
{
    public static DpapiCalendarContextStore CreateDefault() =>
        new(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Glasswork",
                "calendar-context"),
            new DpapiCalendarDataProtector());
}
