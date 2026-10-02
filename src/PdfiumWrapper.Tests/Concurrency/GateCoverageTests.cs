using System.Drawing;
using System.Reflection;
using System.Text.RegularExpressions;

namespace PdfiumWrapper.Tests.Concurrency;

/// <summary>
/// Structural guards for the native gate: every public PDFium-touching member enters it, and no
/// gated scope spans an await or a yield.
/// </summary>
[Collection(PdfTestCollection.Name)]
public class GateCoverageTests : IDisposable
{
    private const string ContractPdf = "Docs/contract.pdf";
    private const string OnePagePdf = "Docs/doc-1-page.pdf";
    private const string FormPdf = "Docs/fw2.pdf";

    private static readonly Type[] GatedTypes =
    {
        typeof(PdfDocument), typeof(PdfPage), typeof(PdfForm), typeof(PdfMerger),
        typeof(PdfMetadata), typeof(PdfBookmarks), typeof(PdfBookmark),
        typeof(PdfAttachments), typeof(PdfAttachment),
        typeof(PdfTextObject), typeof(PdfImageObject), typeof(PdfPathObject),
        typeof(PdfShadingObject), typeof(PdfFormObject),
    };

    private static readonly HashSet<string> ExcludedNames = new()
    {
        nameof(IDisposable.Dispose), nameof(Equals), nameof(GetHashCode), nameof(ToString), nameof(GetType),
    };

    private readonly string _tempDirectory =
        Path.Combine(Path.GetTempPath(), $"PdfiumGateCoverage_{Guid.NewGuid():N}");

    private static readonly Lazy<byte[]> PngBytes = new(() =>
    {
        using var doc = new PdfDocument(OnePagePdf);
        return doc.StreamImageBytes(ImageFormat.Png, 100, 20).First();
    });

    public GateCoverageTests()
    {
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
            Directory.Delete(_tempDirectory, recursive: true);
    }

    [Fact]
    public async Task EveryPublicPdfiumMemberEntersTheGate()
    {
        var notEntered = new List<string>();
        int checkedMembers = 0;

        foreach (var type in GatedTypes)
        {
            if (type.GetCustomAttribute<NoNativeCallAttribute>() != null)
                continue;

            foreach (var method in MembersToCheck(type))
            {
                checkedMembers++;
                if (!await EntersGate(type, method))
                    notEntered.Add($"{type.Name}.{method.Name}({string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name))})");
            }
        }

        Assert.True(checkedMembers > 100, $"Expected to check the whole public surface, checked only {checkedMembers} members.");
        Assert.True(notEntered.Count == 0,
            "These public members call into PDFium without entering PdfiumRuntime (or need [NoNativeCall]): "
            + string.Join("; ", notEntered));
    }

    /// <summary>Public methods and property accessors, instance and static, minus the deliberate exclusions.</summary>
    private static IEnumerable<MethodInfo> MembersToCheck(Type type)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

        var unmarkedAccessors = new HashSet<MethodInfo>();
        var markedAccessors = new HashSet<MethodInfo>();
        foreach (var property in type.GetProperties(flags))
        {
            bool marked = property.GetCustomAttribute<NoNativeCallAttribute>() != null;
            foreach (var accessor in property.GetAccessors(nonPublic: false))
                (marked ? markedAccessors : unmarkedAccessors).Add(accessor);
        }

        foreach (var method in type.GetMethods(flags))
        {
            if (method.DeclaringType == typeof(object) || ExcludedNames.Contains(method.Name))
                continue;
            if (method.GetCustomAttribute<NoNativeCallAttribute>() != null || markedAccessors.Contains(method))
                continue;
            if (method.IsSpecialName && !unmarkedAccessors.Contains(method))
                continue;

            yield return method;
        }
    }

    private async Task<bool> EntersGate(Type type, MethodInfo method)
    {
        using var fixture = new Fixture(type, _tempDirectory);

        var invocable = method.IsGenericMethodDefinition ? method.MakeGenericMethod(typeof(int)) : method;
        var arguments = invocable.GetParameters().Select(p => fixture.Argument(invocable, p)).ToArray();

        long before = PdfiumDiagnostics.GateEntryCount;
        object? result = null;
        try
        {
            result = invocable.Invoke(invocable.IsStatic ? null : fixture.Target, arguments);
        }
        catch (TargetInvocationException)
        {
            // Argument or state errors are fine: the member still has to reach the gate first.
        }

        bool entered = PdfiumDiagnostics.GateEntryCount > before;

        // Run lazy and asynchronous results to completion so nothing is left in flight.
        try
        {
            switch (result)
            {
                case Task task:
                    await task;
                    break;
                case IAsyncEnumerable<byte[]> asyncPages:
                    await foreach (var _ in asyncPages) { }
                    break;
                case IEnumerable<byte[]> pages:
                    foreach (var _ in pages) { }
                    break;
            }
        }
        catch (Exception)
        {
        }

        switch (result)
        {
            case IDisposable disposable:
                disposable.Dispose();
                break;
            case PdfPage[] pagesToDispose:
                foreach (var page in pagesToDispose) page.Dispose();
                break;
        }

        return entered;
    }

    /// <summary>A live object of the type under test plus plausible arguments for its members.</summary>
    private sealed class Fixture : IDisposable
    {
        private readonly List<IDisposable> _owned = new();
        private readonly string _tempDirectory;
        private readonly PdfDocument _document;
        private readonly PdfPage _page;
        private readonly PdfDocument _other;

        public object Target { get; }

        public Fixture(Type type, string tempDirectory)
        {
            _tempDirectory = tempDirectory;
            _other = Own(new PdfDocument(OnePagePdf));

            bool editing = typeof(PdfPageObject).IsAssignableFrom(type);
            _document = Own(type == typeof(PdfForm) ? new PdfDocument(FormPdf)
                : editing ? new PdfDocument()
                : new PdfDocument(ContractPdf));
            _page = editing ? _document.AddPage() : _document.GetPage(0);

            Target = type switch
            {
                _ when type == typeof(PdfDocument) => _document,
                _ when type == typeof(PdfPage) => _page,
                _ when type == typeof(PdfForm) => _document.GetForm()!,
                _ when type == typeof(PdfMerger) => Own(new PdfMerger(OnePagePdf)),
                _ when type == typeof(PdfMetadata) => _document.Metadata,
                _ when type == typeof(PdfBookmarks) => _document.Bookmarks,
                _ when type == typeof(PdfAttachments) => _document.Attachments,
                _ when type == typeof(PdfTextObject) => _page.AddText("gate", 10, 10),
                _ when type == typeof(PdfImageObject) => _page.AddImage(PngBytes.Value, 10, 10, 20, 20),
                _ when type == typeof(PdfPathObject) => _page.AddRectangle(10, 10, 20, 20),
                _ when type == typeof(PdfShadingObject) => WrapAttached((handle, doc) => new PdfShadingObject(handle, doc)),
                _ when type == typeof(PdfFormObject) => WrapAttached((handle, doc) => new PdfFormObject(handle, doc)),
                _ => throw new NotSupportedException(type.Name),
            };
        }

        /// <summary>
        /// PDFium offers no way to create these two kinds, so the wrapper is put around an existing
        /// page-owned object. The native calls reject the mismatched kind; the gate is still entered.
        /// </summary>
        private T WrapAttached<T>(Func<IntPtr, IntPtr, T> create) where T : PdfPageObject
        {
            _page.AddRectangle(10, 10, 20, 20);
            using (PdfiumRuntime.Enter())
            {
                var wrapper = create(_page.GetObject(0), _document.Document);
                wrapper.AttachToPage(_page);
                return wrapper;
            }
        }

        private T Own<T>(T disposable) where T : IDisposable
        {
            _owned.Add(disposable);
            return disposable;
        }

        public object? Argument(MethodInfo method, ParameterInfo parameter)
        {
            var type = parameter.ParameterType;
            string name = parameter.Name ?? string.Empty;

            if (type == typeof(string))
            {
                return name switch
                {
                    "filePath" when method.DeclaringType == typeof(PdfMerger) => Path.GetFullPath(OnePagePdf),
                    "filePath" or "outputPath" => Path.Combine(_tempDirectory, $"{Guid.NewGuid():N}.out"),
                    "outputDirectory" => Path.Combine(_tempDirectory, Guid.NewGuid().ToString("N")),
                    "password" => null,
                    "pageRange" => "1",
                    "tag" => "Title",
                    "font" or "fontName" => "Helvetica",
                    "fieldName" => "no-such-field",
                    _ => "value",
                };
            }

            if (type == typeof(int))
                return name.Contains("dpi", StringComparison.OrdinalIgnoreCase) ? 36
                    : name is "width" or "height" ? 100
                    : name == "quality" ? 80
                    : 0;
            if (type == typeof(uint)) return 0u;
            if (type == typeof(byte)) return (byte)128;
            if (type == typeof(bool)) return false;
            if (type == typeof(float)) return 1f;
            if (type == typeof(double)) return 1d;
            if (type == typeof(IntPtr))
                return name switch
                {
                    "documentHandle" => _document.Document,
                    "page" => _page.Handle,
                    _ => IntPtr.Zero,
                };
            if (type == typeof(Stream)) return Own(new MemoryStream());
            if (type == typeof(Stream[]))
                return Enumerable.Range(0, _document.PageCount).Select(_ => (Stream)Own(new MemoryStream())).ToArray();
            if (type == typeof(byte[])) return name == "imageBytes" ? PngBytes.Value : File.ReadAllBytes(OnePagePdf);
            if (type == typeof(int[])) return new[] { 0 };
            if (type == typeof(string[])) return new[] { "a" };
            if (type == typeof(PdfDocument)) return _other;
            if (type == typeof(PdfPage)) return _page;
            if (type == typeof(PdfPageObject)) return _page.AddRectangle(1, 1, 2, 2);
            if (type == typeof(ImageFormat)) return ImageFormat.Png;
            if (type == typeof(TiffColorMode)) return TiffColorMode.Bilevel;
            if (type == typeof(Color)) return Color.Black;
            if (type == typeof(DateTime)) return DateTime.UtcNow;
            if (type == typeof(Func<PdfPage, int>)) return (Func<PdfPage, int>)(_ => 0);
            if (type == typeof(Action<PdfPage>)) return (Action<PdfPage>)(_ => { });
            if (Nullable.GetUnderlyingType(type) != null) return null;
            if (type.IsEnum) return Enum.GetValues(type).GetValue(0);

            throw new NotSupportedException($"No test argument for {method.DeclaringType?.Name}.{method.Name}({type.Name} {name})");
        }

        public void Dispose()
        {
            for (int i = _owned.Count - 1; i >= 0; i--)
                _owned[i].Dispose();
        }
    }

    [Fact]
    public void NoGatedScopeSpansAnAwaitOrYield()
    {
        var sourceDirectory = FindLibrarySourceDirectory();
        if (sourceDirectory == null)
            return; // Sources are not next to the binaries (packaged test run).

        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*.cs"))
        {
            string text = StripLineComments(File.ReadAllText(file));

            // using (PdfiumRuntime.Enter()) { ... } and using (await PdfiumRuntime.EnterAsync()) { ... }
            foreach (Match match in Regex.Matches(text, @"using\s*\(\s*(?:await\s+)?PdfiumRuntime\.Enter(?:Async)?\([^)]*\)\s*\)"))
            {
                int open = text.IndexOf('{', match.Index + match.Length);
                string trivia = text[(match.Index + match.Length)..open];
                Assert.True(string.IsNullOrWhiteSpace(trivia), $"{Path.GetFileName(file)}: a gated using must be followed by a block.");
                CheckBody(file, text, open + 1, violations);
            }

            // using var _ = PdfiumRuntime.Enter(); holds the gate to the end of the enclosing block.
            foreach (Match match in Regex.Matches(text, @"using\s+var\s+\w+\s*=\s*(?:await\s+)?PdfiumRuntime\.Enter(?:Async)?\([^)]*\)\s*;"))
            {
                CheckBody(file, text, match.Index + match.Length, violations);
            }
        }

        Assert.True(violations.Count == 0,
            "A gated scope must not contain await or yield return: " + string.Join("; ", violations));
    }

    /// <summary>Scans from <paramref name="start"/> to the brace that closes the enclosing block.</summary>
    private static void CheckBody(string file, string text, int start, List<string> violations)
    {
        int depth = 0;
        int end = start;
        for (; end < text.Length; end++)
        {
            if (text[end] == '{') depth++;
            else if (text[end] == '}' && --depth < 0) break;
        }

        string body = text[start..end];
        if (Regex.IsMatch(body, @"\bawait\b|\byield\s+return\b"))
        {
            int line = text[..start].Count(c => c == '\n') + 1;
            violations.Add($"{Path.GetFileName(file)}:{line}");
        }
    }

    private static string StripLineComments(string text)
        => Regex.Replace(text, @"//[^\n]*", string.Empty);

    private static string? FindLibrarySourceDirectory()
    {
        var testProject = TestProjectHygieneTests.FindProjectDirectory();
        if (testProject == null)
            return null;

        var library = Path.Combine(Path.GetDirectoryName(testProject)!, "PdfiumWrapper");
        return File.Exists(Path.Combine(library, "PdfiumWrapper.csproj")) ? library : null;
    }
}
