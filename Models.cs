using System;

namespace Monitor
{
    public class TimeInterval
    {
        public TimeSpan Start { get; set; }
        public TimeSpan End { get; set; }
        public string Type { get; set; } // "Gaming" or "School"

        public bool IsActive(TimeSpan time)
        {
            if (Start <= End)
            {
                return time >= Start && time < End;
            }
            else
            {
                // Crosses midnight
                return time >= Start || time < End;
            }
        }
    }
}
