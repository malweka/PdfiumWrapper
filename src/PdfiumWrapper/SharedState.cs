namespace PdfiumWrapper;

/// <summary>
/// Process-wide state shared between copies of this assembly loaded into different
/// <see cref="System.Runtime.Loader.AssemblyLoadContext"/>s. All copies drive the single native
/// <c>pdfium</c> module, so they must agree on one gate, one init flag and one set of queues.
/// Values live in <see cref="AppContext"/> data under fixed keys and are BCL types only, so every
/// copy sees the same type identity.
/// </summary>
internal static class SharedState
{
    public static T GetOrCreate<T>(string key, Func<T> create) where T : class
    {
        // AppContext has no atomic get-or-add. AppDomain.CurrentDomain is the one object every
        // copy of this assembly can name, so it serves as the bootstrap lock. Held only during
        // type initialization.
        lock (AppDomain.CurrentDomain)
        {
            if (AppContext.GetData(key) is T existing)
                return existing;

            var created = create();
            AppContext.SetData(key, created);
            return created;
        }
    }
}

/// <summary>
/// Marks a public member (or a whole type) that does not call into PDFium and therefore does not
/// enter the native gate. Lets the gate-coverage test skip it deliberately rather than silently.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Class)]
internal sealed class NoNativeCallAttribute : Attribute
{
}
