using System.Globalization;
using System.Resources;
using Avalonia.Markup.Xaml;

namespace Iseberg;

public static class UiText
{
    private static readonly ResourceManager Resources = new("Iseberg.Resources.Strings", typeof(UiText).Assembly);
    public static string Get(string key) => Resources.GetString(key, CultureInfo.CurrentUICulture)
        ?? throw new MissingManifestResourceException($"Missing UI resource: {key}");
}

public sealed class UiExtension(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => UiText.Get(key);
}
