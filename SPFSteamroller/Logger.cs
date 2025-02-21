using System;
using System.IO;

namespace SPFSteamroller
{
    /// <summary>
    /// Provides logging functionality with console output and file logging capabilities.
    /// </summary>
    public static class Logger
    {
        /// <summary>
        /// The file path where log entries are written.
        /// </summary>
        private const string LogFile = "log.txt";

        /// <summary>
        /// Writes a log entry to the log file.
        /// </summary>
        /// <param name="level">The log level (ERROR, WARNING, INFO).</param>
        /// <param name="message">The message to log.</param>
        /// <remarks>
        /// This method silently fails if it cannot write to the log file to prevent application crashes.
        /// </remarks>
        private static void WriteToFile(string level, string message)
        {
            try
            {
                string logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}";
                File.AppendAllText(LogFile, logEntry + Environment.NewLine);
            }
            catch
            {
                // Silently fail if we can't write to the log file
            }
        }

        /// <summary>
        /// Logs an error message with visual emphasis using red text.
        /// </summary>
        /// <param name="message">The error message to log.</param>
        /// <remarks>
        /// Error messages are both written to the console and the log file.
        /// </remarks>
        public static void Error(string message)
        {
            Console.Write("[ERROR] ");
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(message);
            ResetColors();
            WriteToFile("ERROR", message);
        }

        /// <summary>
        /// Logs a warning message with visual emphasis using yellow text.
        /// </summary>
        /// <param name="message">The warning message to log.</param>
        /// <remarks>
        /// Warning messages are both written to the console and the log file.
        /// </remarks>
        public static void Warning(string message)
        {
            Console.Write("[WARNING] ");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(message);
            ResetColors();
            WriteToFile("WARNING", message);
        }

        /// <summary>
        /// Logs an informational message with visual emphasis using blue text.
        /// </summary>
        /// <param name="message">The informational message to log.</param>
        /// <remarks>
        /// Information messages are both written to the console and the log file.
        /// </remarks>
        public static void Info(string message)
        {
            Console.Write("[INFO] ");
            Console.ForegroundColor = ConsoleColor.Blue;
            Console.WriteLine(message);
            ResetColors();
            WriteToFile("INFO", message);
        }

        /// <summary>
        /// Resets the console colors to their defaults.
        /// </summary>
        private static void ResetColors()
        {
            Console.ResetColor();
        }
    }
}
