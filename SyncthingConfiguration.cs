using System.Xml.Linq;

namespace SyncthingNotifier;

internal sealed record SyncthingConfiguration(
    Uri GuiUri,
    string ApiKey,
    IReadOnlyDictionary<string, string> Folders);

internal static class SyncthingConfigurationReader
{
    public static SyncthingConfiguration Load()
    {
        var configPath = FindConfigPath();
        if (configPath is null)
        {
            throw new InvalidOperationException(
                "Syncthing's config.xml was not found. Start Syncthing on this PC, then retry.");
        }

        try
        {
            var document = XDocument.Load(configPath);
            var root = document.Root ?? throw new InvalidOperationException("The configuration file is empty.");
            var gui = Child(root, "gui")
                ?? throw new InvalidOperationException("The Syncthing GUI configuration is missing.");

            if (!bool.TryParse(gui.Attribute("enabled")?.Value, out var guiEnabled) || !guiEnabled)
            {
                throw new InvalidOperationException("Syncthing's local Web GUI is disabled.");
            }

            var address = ChildValue(gui, "address");
            var apiKey = ChildValue(gui, "apikey");
            if (string.IsNullOrWhiteSpace(address))
            {
                throw new InvalidOperationException("Syncthing's local Web GUI address is missing.");
            }

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException("Syncthing's GUI API key is missing.");
            }

            var usesTls = bool.TryParse(gui.Attribute("tls")?.Value, out var tls) && tls;
            var guiUri = CreateGuiUri(address, usesTls);
            var folders = root.Elements()
                .Where(element => element.Name.LocalName == "folder")
                .Select(folder => new
                {
                    Id = folder.Attribute("id")?.Value,
                    Label = folder.Attribute("label")?.Value,
                    Path = folder.Attribute("path")?.Value
                })
                .Where(folder => !string.IsNullOrWhiteSpace(folder.Id))
                .ToDictionary(
                    folder => folder.Id!,
                    folder => string.IsNullOrWhiteSpace(folder.Label)
                        ? (Path.GetFileName(folder.Path?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? folder.Id!)
                        : folder.Label!,
                    StringComparer.Ordinal);

            return new SyncthingConfiguration(guiUri, apiKey, folders);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            throw new InvalidOperationException($"Could not read Syncthing's config.xml: {exception.Message}", exception);
        }
    }

    private static string? FindConfigPath()
    {
        var paths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Syncthing", "config.xml"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Syncthing", "config.xml")
        };

        return paths.FirstOrDefault(File.Exists);
    }

    private static XElement? Child(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(element => element.Name.LocalName == localName);

    private static string? ChildValue(XElement parent, string localName) => Child(parent, localName)?.Value.Trim();

    private static Uri CreateGuiUri(string address, bool usesTls)
    {
        if (Uri.TryCreate(address, UriKind.Absolute, out var absoluteUri))
        {
            return EnsureTrailingSlash(absoluteUri);
        }

        var scheme = usesTls ? Uri.UriSchemeHttps : Uri.UriSchemeHttp;
        if (!Uri.TryCreate($"{scheme}://{address.TrimEnd('/')}/", UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("Syncthing's local Web GUI address is invalid.");
        }

        return uri;
    }

    private static Uri EnsureTrailingSlash(Uri uri) =>
        uri.AbsoluteUri.EndsWith('/') ? uri : new Uri($"{uri.AbsoluteUri}/");
}
