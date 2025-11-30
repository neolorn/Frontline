namespace Frontline.Services;

internal static class ArgUtils
{
    private static string Quote(string value)
    {
        return value.Any(char.IsWhiteSpace) ? $"\"{value.Replace("\"", "\\\"")}\"" : value;
    }

    internal static string Concatenate(IEnumerable<string> args)
    {
        return string.Join(' ', args.Select(Quote));
    }
}