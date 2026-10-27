using System.Windows;

namespace Pulse.Services;

public static class Localization
{
    public static string Get(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;

    public static string Get(string key, string fallback) =>
        Application.Current?.TryFindResource(key) as string ?? fallback;

    public static void Apply(string language)
    {
        var dictionaries = Application.Current?.Resources.MergedDictionaries;
        if (dictionaries is null)
        {
            return;
        }

        var index = dictionaries
            .Select((dictionary, position) => (dictionary, position))
            .FirstOrDefault(item => item.dictionary.Source?.OriginalString.Contains("Strings.", StringComparison.Ordinal) == true)
            .position;

        var resource = new ResourceDictionary
        {
            Source = new Uri($"Themes/Strings.{(language == "ko" ? "ko" : "en")}.xaml", UriKind.Relative)
        };

        var existing = dictionaries.FirstOrDefault(dictionary =>
            dictionary.Source?.OriginalString.Contains("Strings.", StringComparison.Ordinal) == true);
        if (existing is not null)
        {
            dictionaries[index] = resource;
        }
        else
        {
            dictionaries.Add(resource);
        }
    }
}
