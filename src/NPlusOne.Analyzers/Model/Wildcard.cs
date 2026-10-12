namespace NPlusOne.Analyzers.Model;

/// <summary>
/// Matches names against patterns where <c>*</c> stands for any run of characters. Written by hand
/// because a regular expression per pattern and name would allocate on every call.
/// </summary>
internal static class Wildcard
{
    /// <summary>Whether <paramref name="text"/> matches <paramref name="pattern"/>, ordinally.</summary>
    /// <param name="pattern">The pattern; <c>*</c> is the only special character.</param>
    /// <param name="text">The name to test.</param>
    public static bool IsMatch(string pattern, string text)
    {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && pattern[p] != '*' && pattern[p] == text[t])
            {
                p++;
                t++;
                continue;
            }

            if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
                continue;
            }

            if (star < 0)
            {
                return false;
            }

            p = star + 1;
            t = ++mark;
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }
}
