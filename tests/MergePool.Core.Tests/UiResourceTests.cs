using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace MergePool.Core.Tests;

/// <summary>
/// Guards the window's resources by reading its XAML as XML. A missing key, or a colour one theme
/// defines and the other does not, compiles perfectly and then throws the moment the window is
/// built — which no compile-only CI job would ever notice.
/// </summary>
public sealed class UiResourceTests
{
    private static readonly Regex ResourceReference =
        new(@"\{(?:Static|Dynamic)Resource\s+([A-Za-z0-9_.]+)\}", RegexOptions.Compiled);

    private static readonly Regex KeyDeclaration =
        new(@"x:Key=""([^""]+)""", RegexOptions.Compiled);

    [Fact]
    public void Every_resource_the_window_asks_for_is_defined()
    {
        var defined = Declared("Themes/Theme.xaml")
            .Union(Declared("Themes/Light.xaml"))
            .Union(Declared("Themes/Dark.xaml"))
            .Union(Declared("App.xaml"))
            .ToHashSet(StringComparer.Ordinal);

        var referenced = Referenced("Views/MainWindow.xaml")
            .Union(Referenced("Themes/Theme.xaml"))
            .ToHashSet(StringComparer.Ordinal);

        var missing = referenced.Except(defined).Order(StringComparer.Ordinal).ToArray();

        Assert.True(
            missing.Length == 0,
            $"The UI references resources nothing defines: {string.Join(", ", missing)}");
    }

    [Fact]
    public void The_two_palettes_define_exactly_the_same_names()
    {
        // The theme is swapped by replacing one dictionary with the other at runtime. A name in only
        // one of them means that colour vanishes the moment someone switches theme.
        var light = Declared("Themes/Light.xaml");
        var dark = Declared("Themes/Dark.xaml");

        Assert.Equal(light.Order(StringComparer.Ordinal), dark.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Every_xaml_file_is_well_formed()
    {
        foreach (var file in Directory.EnumerateFiles(UiRoot(), "*.xaml", SearchOption.AllDirectories))
        {
            var exception = Record.Exception(() => XDocument.Load(file));
            Assert.True(exception is null, $"{Path.GetFileName(file)} is not valid XML: {exception?.Message}");
        }
    }

    [Fact]
    public void No_style_is_set_both_as_an_attribute_and_as_a_property_element()
    {
        // WPF rejects an element that carries Style="..." and a <X.Style> child: the property would
        // be set twice. It is an easy thing to introduce when adding triggers to a styled element.
        foreach (var file in Directory.EnumerateFiles(UiRoot(), "*.xaml", SearchOption.AllDirectories))
        {
            var document = XDocument.Load(file);

            foreach (var element in document.Descendants())
            {
                var hasAttribute = element.Attribute("Style") is not null;
                var hasPropertyElement = element.Elements()
                    .Any(child => child.Name.LocalName == element.Name.LocalName + ".Style");

                Assert.False(
                    hasAttribute && hasPropertyElement,
                    $"{Path.GetFileName(file)}: <{element.Name.LocalName}> sets Style twice.");
            }
        }
    }

    private static HashSet<string> Declared(string relativePath) =>
        Matches(relativePath, KeyDeclaration);

    private static HashSet<string> Referenced(string relativePath) =>
        Matches(relativePath, ResourceReference);

    private static HashSet<string> Matches(string relativePath, Regex pattern)
    {
        var text = File.ReadAllText(Path.Combine(UiRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

        return pattern
            .Matches(text)
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string UiRoot() => Path.Combine(RepositoryRoot(), "src", "MergePool.Ui");

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MergePool.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root from the test output directory.");
    }
}
