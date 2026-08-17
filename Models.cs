using System;
using System.Collections.Generic;

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

    public class DailyStatsData
    {
        public string Date { get; set; } = "";
        public int TotalComputerSeconds { get; set; } = 0;
        public int TotalScreenSeconds { get; set; } = 0;
        public int TotalGamingSeconds { get; set; } = 0;
        public int AvailableGamingSeconds { get; set; } = 0;
        public int GrantedBonusSeconds { get; set; } = 0;
        public Dictionary<string, int> AppSeconds { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> AudioSeconds { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> GameSeconds { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public int RemainingGamingSeconds => Math.Max(0, AvailableGamingSeconds - TotalGamingSeconds);
    }
}
