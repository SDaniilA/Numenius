namespace Numenius.Core.Models
{
    /// <summary>
    /// Константы приоритетов для сообщений
    /// </summary>
    public static class Priority
    {
        public const int RocketMissile = 0;     // Ракетная опасность, пуски, прилёты
        public const int StrikeDrone = 1;       // Ударные БПЛА (Hornet, Dart)
        public const int ReconDrone = 2;        // Разведчики (Shark, Leleka)
        public const int FPV_Activity = 3;      // FPV-активность, общие тревоги
        public const int WatchTerminate = 4;    // Отбой, режим внимания, уничтожен
        public const int LowPriority = 5;       // Пролёты в тыл, сводки, погода
    }
}