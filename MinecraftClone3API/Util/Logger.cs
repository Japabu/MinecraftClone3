using System;

namespace MinecraftClone3API.Util
{
    public static class Logger
    {
        public static void Info(string message) => Log(ConsoleColor.White, "Info", message);
        public static void Debug(string message) => Log(ConsoleColor.Gray, "Debug", message);
        public static void Warn(string message) => Log(ConsoleColor.DarkYellow, "Warn", message);
        public static void Error(string message) => Log(ConsoleColor.Red, "Error", message);

        public static void Exception(Exception ex)
            => Log(ConsoleColor.Yellow, "Exception",
                $"An unexpected exception has been thrown: {ex}\n\n{ex.StackTrace}\n");

        private static void Log(ConsoleColor color, string level, string message)
        {
            Console.ForegroundColor = color;
            Console.Write($"[{level}]: ");
            Console.ResetColor();
            Console.WriteLine(message);
        }
    }
}