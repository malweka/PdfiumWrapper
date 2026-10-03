using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace PdfiumWrapper.Tests;

/// <summary>
/// The low-priority audit findings at the native boundary: import signatures (AUD-018), the
/// document identifier (AUD-019), the public surface (AUD-020), the shared UTF-16 reader
/// (AUD-021), typed load errors (AUD-022) and cancellation of the async exports (AUD-023).
/// </summary>
[Collection(PdfTestCollection.Name)]
public class NativeBoundaryTests
{
    private const string OnePagePdf = "Docs/doc-1-page.pdf";
    private const string ContractPdf = "Docs/contract.pdf";
    private const string EncryptedPdf = "Docs/encrypted.pdf";
    private const string EncryptedPdfPassword = "secret";

    #region Import signatures (AUD-018)

    private static IEnumerable<(MethodInfo Method, LibraryImportAttribute Import)> Imports() =>
        typeof(PDFium).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Select(method => (method, import: method.GetCustomAttribute<LibraryImportAttribute>()))
            .Where(entry => entry.import != null)
            .Select(entry => (entry.method, entry.import!));

    [Fact]
    public void EveryImport_IsExportedByItsNativeLibrary()
    {
        NativeLibraryResolver.EnsureRegistered();
        var imports = Imports().ToList();
        Assert.True(imports.Count > 150, $"Only {imports.Count} imports found; is LibraryImportAttribute still in metadata?");

        var handles = new Dictionary<string, IntPtr>();
        var missing = new List<string>();
        foreach (var (method, import) in imports)
        {
            if (!handles.TryGetValue(import.LibraryName, out var handle))
                handles[import.LibraryName] = handle = NativeLibrary.Load(import.LibraryName, typeof(PDFium).Assembly, null);

            string entryPoint = import.EntryPoint ?? method.Name;
            if (!NativeLibrary.TryGetExport(handle, entryPoint, out _))
                missing.Add($"{import.LibraryName}!{entryPoint}");
        }

        // A missing export only fails when the import is first called, as EntryPointNotFoundException.
        Assert.Empty(missing);
    }

    [Fact]
    public void PdfiumImportsAndStructs_UseCLongForCLong()
    {
        // C long and unsigned long are 4 bytes on Windows and 8 on Linux and macOS, so they must be
        // CLong/CULong. PDFium has no other 64-bit integer in its API (size_t is nuint).
        static bool IsInt64(Type type) => (type.IsByRef ? type.GetElementType()! : type) is var t
            && (t == typeof(long) || t == typeof(ulong));

        var offenders = Imports()
            .Where(entry => entry.Import.LibraryName == "pdfium")
            .SelectMany(entry => entry.Method.GetParameters()
                .Where(parameter => IsInt64(parameter.ParameterType))
                .Select(parameter => $"{entry.Method.Name}({parameter.Name})")
                .Concat(IsInt64(entry.Method.ReturnType) ? new[] { $"{entry.Method.Name} return" } : []))
            .Concat(typeof(PDFium).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
                .Where(type => type.IsValueType && !type.IsEnum)
                .SelectMany(type => type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Where(field => IsInt64(field.FieldType))
                    .Select(field => $"{type.Name}.{field.Name}")))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void AttachmentKeys_ReachPdfiumAsByteStrings()
    {
        // FPDF_BYTESTRING keys sent as UTF-16 arrived as "S" for "Size".
        using var doc = new PdfDocument();
        using (PdfiumRuntime.Enter())
        {
            var attachment = PDFium.FPDFDoc_AddAttachment(doc.Document, "a.txt");
            var contents = "hello"u8.ToArray();
            Assert.True(PDFium.FPDFAttachment_SetFile(attachment, doc.Document, contents, new CULong((uint)contents.Length)));

            // SetFile records the size and an MD5 checksum in the file's parameters.
            Assert.True(PDFium.FPDFAttachment_HasKey(attachment, "Size"));
            Assert.False(PDFium.FPDFAttachment_HasKey(attachment, "NoSuchKey"));
            var checksum = NativeText.ReadUtf16(attachment,
                static (handle, buffer, length) => PDFium.FPDFAttachment_GetStringValue(handle, "CheckSum", buffer, length));
            Assert.False(string.IsNullOrEmpty(checksum));
        }
    }

    #endregion

    #region Document identifier (AUD-019)

    [Fact]
    public void DocumentId_IsTheIdentifierWithoutItsTerminator()
    {
        using var doc = new PdfDocument(ContractPdf);

        var id = doc.DocumentId;

        // A 16-byte trailer ID: 32 hex characters, not 34 ending in "00".
        Assert.NotNull(id);
        Assert.Equal(32, id.Length);
    }

    #endregion

    #region Public surface (AUD-020)

    [Fact]
    public void PublicSurface_DoesNotTakeOrReturnNativePointers()
    {
        static bool IsNative(Type type)
        {
            while (type.HasElementType)
                type = type.GetElementType()!;
            return type == typeof(IntPtr) || type == typeof(UIntPtr) || type.IsPointer || type.IsFunctionPointer;
        }

        static bool IsVisible(MethodBase? method) => method != null && (method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly);

        var offenders = new List<string>();
        foreach (var type in typeof(PdfDocument).Assembly.GetExportedTypes())
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (var method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)).Where(IsVisible))
            {
                if ((method is MethodInfo info && IsNative(info.ReturnType)) || method.GetParameters().Any(p => IsNative(p.ParameterType)))
                    offenders.Add($"{type.Name}.{method.Name}");
            }

            offenders.AddRange(type.GetFields(all)
                .Where(field => (field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly) && IsNative(field.FieldType))
                .Select(field => $"{type.Name}.{field.Name}"));
            offenders.AddRange(type.GetProperties(all)
                .Where(property => (IsVisible(property.GetMethod) || IsVisible(property.SetMethod)) && IsNative(property.PropertyType))
                .Select(property => $"{type.Name}.{property.Name}"));
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void GetObject_WrapsThePageObjectInItsType_OnceWhileTheWrapperLives()
    {
        using var doc = new PdfDocument();
        using var page = doc.AddPage();
        var text = page.AddText("hello", 10, 10);
        var rectangle = page.AddRectangle(10, 10, 20, 20);

        // Objects the wrapper added come back as the same instances.
        Assert.Same(text, page.GetObject(0));
        Assert.Same(rectangle, page.GetObject(1));
    }

    [Fact]
    public void GetObject_OnLoadedPage_ReturnsTypedWrappersOwnedByThePage()
    {
        using var doc = new PdfDocument(ContractPdf);
        var page = doc.GetPage(0);
        Assert.True(page.ObjectCount > 0);

        var first = page.GetObject(0);
        Assert.IsAssignableFrom<PdfPageObject>(first);
        Assert.NotEqual(typeof(PdfPageObject), first.GetType());
        Assert.Same(first, page.GetObject(0));
        _ = first.GetBounds();

        // Disposing the wrapper does not delete the object; the next call wraps it again.
        int count = page.ObjectCount;
        first.Dispose();
        Assert.Equal(count, page.ObjectCount);
        var again = page.GetObject(0);
        Assert.NotSame(first, again);

        page.Dispose();
        Assert.Throws<ObjectDisposedException>(() => again.GetBounds());
    }

    #endregion

    #region UTF-16 reader (AUD-021)

    [Fact]
    public void ReadUtf16_ReadsByLengthAndDropsTheTerminator()
    {
        Assert.Equal("abc", ReadFake("abc\0"));
        Assert.Equal("", ReadFake("\0"));
        Assert.Null(NativeText.ReadUtf16(0, static (_, _, _) => default));
    }

    [Fact]
    public void ReadUtf16_RejectsALengthAStringCannotHave()
    {
        Assert.Throws<InvalidDataException>(() => NativeText.ReadUtf16(0, static (_, _, _) => new CULong(3)));
    }

    /// <summary>Plays PDFium: reports the byte length of <paramref name="value"/>, then copies it.</summary>
    private static string? ReadFake(string value) =>
        NativeText.ReadUtf16(Encoding.Unicode.GetBytes(value), static (bytes, buffer, length) =>
        {
            if (buffer != IntPtr.Zero && length.Value >= (nuint)bytes.Length)
                Marshal.Copy(bytes, 0, buffer, bytes.Length);
            return new CULong((uint)bytes.Length);
        });

    #endregion

    #region Load errors (AUD-022)

    [Fact]
    public void Load_WithoutOrWithWrongPassword_ReportsPasswordError()
    {
        var noPassword = Assert.Throws<PdfiumException>(() => new PdfDocument(EncryptedPdf));
        Assert.Equal(PdfiumErrorCode.Password, noPassword.ErrorCode);
        Assert.IsAssignableFrom<InvalidOperationException>(noPassword);

        var wrongPassword = Assert.Throws<PdfiumException>(() => new PdfDocument(File.ReadAllBytes(EncryptedPdf), "wrong"));
        Assert.Equal(PdfiumErrorCode.Password, wrongPassword.ErrorCode);

        using var doc = new PdfDocument(EncryptedPdf, EncryptedPdfPassword);
        Assert.Equal(1, doc.PageCount);
    }

    [Fact]
    public void Load_NotAPdf_ReportsFormatError()
    {
        var error = Assert.Throws<PdfiumException>(() => new PdfDocument("not a pdf"u8.ToArray()));
        Assert.Equal(PdfiumErrorCode.Format, error.ErrorCode);
    }

    [Fact]
    public void Load_MissingFile_ReportsFileError()
    {
        var path = Path.Combine(Path.GetTempPath(), $"missing_{Guid.NewGuid():N}.pdf");
        var error = Assert.Throws<PdfiumException>(() => new PdfDocument(path));
        Assert.Equal(PdfiumErrorCode.File, error.ErrorCode);
    }

    #endregion

    #region Cancellation (AUD-023)

    [Fact]
    public async Task AsyncExports_WithCancelledToken_StopBeforeAnyWork()
    {
        using var doc = new PdfDocument(ContractPdf);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var directory = Path.Combine(Path.GetTempPath(), $"PdfiumCancel_{Guid.NewGuid():N}");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => doc.RenderPagesAsync(72, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => doc.SaveAsPngsAsync(directory, dpi: 72, cancellationToken: cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => doc.SaveAsTiffAsync(new MemoryStream(), cancellationToken: cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => doc.ProcessAllPagesAsync(_ => { }, cancelled.Token));
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task AsyncExports_CancelledBetweenPages_StopAtTheNextPage()
    {
        using var doc = new PdfDocument(ContractPdf);
        Assert.True(doc.PageCount >= 2);

        using var cts = new CancellationTokenSource();
        int processed = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => doc.ProcessAllPagesAsync(_ =>
        {
            processed++;
            cts.Cancel();
        }, cts.Token));
        Assert.Equal(1, processed);

        using var streamCts = new CancellationTokenSource();
        int streamed = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in doc.StreamImageBytesAsync(ImageFormat.Png, dpi: 36).WithCancellation(streamCts.Token))
            {
                streamed++;
                streamCts.Cancel();
            }
        });
        Assert.Equal(1, streamed);
    }

    #endregion
}
