using System;
using System.Linq;

namespace SPFSteamroller
{
    /// <summary>
    /// Provides extension methods for string operations.
    /// </summary>
    public static class StringExtensions
    {
        /// <summary>
        /// Repeats the specified string a specified number of times.
        /// </summary>
        /// <param name="text">The string to repeat.</param>
        /// <param name="count">The number of times to repeat the string.</param>
        /// <returns>A new string containing the input string repeated the specified number of times.</returns>
        /// <exception cref="ArgumentException">Thrown when count is negative.</exception>
        public static string Repeat(this string text, int count)
        {
            if (count < 0)
                throw new ArgumentException("Count cannot be negative", nameof(count));
                
            return string.Concat(Enumerable.Repeat(text, count));
        }
    }
}
