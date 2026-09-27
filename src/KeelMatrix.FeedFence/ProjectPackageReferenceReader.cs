using System.Xml;

namespace KeelMatrix.FeedFence;

internal static class ProjectPackageReferenceReader
{
    private const int MaxBytes = 4 * 1024 * 1024;
    private const int MaxPackageReferences = 10_000;

    public static IReadOnlySet<string> Read(string projectPath)
    {
        var fileInfo = new FileInfo(projectPath);
        if (fileInfo.Length > MaxBytes)
        {
            throw new AnalysisException("a project file exceeds the supported input size.");
        }

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxBytes,
                IgnoreComments = true,
                IgnoreWhitespace = true
            };
            using var reader = XmlReader.Create(projectPath, settings);
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element ||
                    !string.Equals(reader.LocalName, "PackageReference", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var attributeName in new[] { "Include", "Update" })
                {
                    var value = reader.GetAttribute(attributeName);
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        continue;
                    }

                    foreach (var packageId in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (result.Count >= MaxPackageReferences)
                        {
                            throw new AnalysisException("the project declares too many package references.");
                        }

                        result.Add(InputIdentity.RequirePackageId(packageId, "the project declares an invalid package identity."));
                    }
                }
            }
        }
        catch (AnalysisException)
        {
            throw;
        }
        catch
        {
            throw new AnalysisException("the project file is malformed or its package references could not be read.");
        }

        return result;
    }
}

internal static class InputIdentity
{
    public static string RequirePackageId(string value, string message)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-' and not '_'))
        {
            throw new AnalysisException(message);
        }

        return value;
    }
}
