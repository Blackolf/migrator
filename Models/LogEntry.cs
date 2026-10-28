namespace SystemMigrator.Models
{
    public enum LogLevel { Info, Success, Error }

    public class LogEntry
    {
        public string Time { get; set; } = DateTime.Now.ToString("HH:mm:ss");
        public string Message { get; set; } = "";
        public LogLevel Level { get; set; } = LogLevel.Info;
    }
}