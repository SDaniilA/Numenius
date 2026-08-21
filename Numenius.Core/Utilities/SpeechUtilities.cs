using System;

namespace Numenius.Core.Utilities
{
    public static class SpeechUtilities
    {
        public static string FormatTimeForSpeech(DateTime now, string format = "hours_minutes")
        {
            if (format == "none") return "";
            if (format == "hours_minutes")
            {
                int hours = now.Hour;
                int minutes = now.Minute;
                string hoursWord = GetHourWord(hours);
                string minutesWord = GetMinuteWord(minutes);
                return $"{hours} {hoursWord}, {minutes} {minutesWord}.";
            }
            if (format == "minutes_only")
                return now.ToString("HH") + " часов " + now.ToString("mm") + " минут.";
            return "";
        }

        private static string GetHourWord(int hours)
        {
            if (hours % 10 == 1 && hours != 11) return "час";
            if (hours % 10 >= 2 && hours % 10 <= 4 && (hours < 12 || hours > 14)) return "часа";
            return "часов";
        }

        private static string GetMinuteWord(int minutes)
        {
            if (minutes % 10 == 1 && minutes != 11) return "минута";
            if (minutes % 10 >= 2 && minutes % 10 <= 4 && (minutes < 12 || minutes > 14)) return "минуты";
            return "минут";
        }
    }
}