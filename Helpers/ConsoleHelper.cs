using System;

namespace ReviewMaker.Helpers
{
    public static class ConsoleHelper
    {
        public static void WriteColor(string text, ConsoleColor color)
        {
            var c = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.Write(text);
            Console.ForegroundColor = c;
        }

        public static void WriteLineColor(string text, ConsoleColor color)
        {
            var c = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine(text);
            Console.ForegroundColor = c;
        }
    }
}