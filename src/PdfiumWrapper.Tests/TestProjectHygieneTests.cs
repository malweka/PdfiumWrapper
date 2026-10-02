using System.Reflection;
using System.Text.RegularExpressions;

namespace PdfiumWrapper.Tests;

/// <summary>
/// PDFium keeps process-wide native state, so every class that touches the wrapper must live in the
/// one xUnit collection. A class outside it would run in parallel with the others.
/// </summary>
[Collection(PdfTestCollection.Name)]
public class TestProjectHygieneTests
{
    private static readonly Assembly WrapperAssembly = typeof(PdfDocument).Assembly;

    [Fact]
    public void AllPdfiumTestClassesShareTheCollection()
    {
        var offenders = new List<string>();

        foreach (var type in typeof(TestProjectHygieneTests).Assembly.GetTypes())
        {
            if (!type.IsClass || type.IsNested || (type.IsAbstract && type.IsSealed))
                continue;
            if (type.Name.StartsWith('<'))
                continue;
            if (!HasTestMethods(type) && !SignaturesReferenceWrapper(type))
                continue;
            if (!IsInPdfCollection(type))
                offenders.Add(type.FullName!);
        }

        foreach (var (file, className) in SourceClassesUsingWrapper())
        {
            var type = typeof(TestProjectHygieneTests).Assembly.GetTypes().FirstOrDefault(t => t.Name == className && !t.IsNested);
            if (type == null || (type.IsAbstract && type.IsSealed) || !type.IsPublic)
                continue;
            if (!IsInPdfCollection(type) && !offenders.Contains(type.FullName!))
                offenders.Add($"{type.FullName} ({Path.GetFileName(file)})");
        }

        Assert.True(offenders.Count == 0,
            $"These classes use PdfiumWrapper but are not in [Collection(\"{PdfTestCollection.Name}\")]: {string.Join(", ", offenders)}");
    }

    private static bool IsInPdfCollection(Type type)
        => type.GetCustomAttributesData().Any(a =>
            a.AttributeType == typeof(CollectionAttribute)
            && a.ConstructorArguments.Count == 1
            && (string?)a.ConstructorArguments[0].Value == PdfTestCollection.Name);

    private static bool HasTestMethods(Type type)
        => type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Any(m => m.GetCustomAttributes<FactAttribute>(inherit: true).Any());

    private static bool SignaturesReferenceWrapper(Type type)
    {
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
                                 | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        if (!type.IsPublic)
            return false;

        return type.GetMethods(all).Any(m => FromWrapper(m.ReturnType) || m.GetParameters().Any(p => FromWrapper(p.ParameterType)))
               || type.GetFields(all).Any(f => FromWrapper(f.FieldType))
               || type.GetProperties(all).Any(p => FromWrapper(p.PropertyType));
    }

    private static bool FromWrapper(Type type)
    {
        if (type.HasElementType)
            return FromWrapper(type.GetElementType()!);
        if (type.IsGenericType && type.GetGenericArguments().Any(FromWrapper))
            return true;
        return type.Assembly == WrapperAssembly;
    }

    /// <summary>
    /// Source scan for classes whose bodies construct wrapper objects without exposing them in a signature.
    /// Skipped when the sources are not next to the binaries.
    /// </summary>
    private static IEnumerable<(string file, string className)> SourceClassesUsingWrapper()
    {
        var projectDir = FindProjectDirectory();
        if (projectDir == null)
            yield break;

        var usesWrapper = new Regex(@"\bnew\s+(PdfDocument|PdfMerger)\s*\(", RegexOptions.Compiled);
        var classDeclaration = new Regex(@"^\s*public\s+(?:sealed\s+)?class\s+(\w+)", RegexOptions.Compiled | RegexOptions.Multiline);

        foreach (var file in Directory.EnumerateFiles(projectDir, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(projectDir, file);
            if (relative.StartsWith("bin", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("obj", StringComparison.OrdinalIgnoreCase))
                continue;

            var text = File.ReadAllText(file);
            if (!usesWrapper.IsMatch(text))
                continue;

            foreach (Match match in classDeclaration.Matches(text))
                yield return (file, match.Groups[1].Value);
        }
    }

    internal static string? FindProjectDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PdfiumWrapper.Tests.csproj")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}

internal static class PdfTestCollection
{
    public const string Name = "PDF Tests";
}
