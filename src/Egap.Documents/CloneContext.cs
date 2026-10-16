namespace Egap.Documents;

/// <summary>
/// One clone or import: the document the copies go to. Tasks 3 and 4 add the styles and lists already brought over, so an
/// import copies each one once.
/// </summary>
internal sealed class CloneContext(Document target)
{
    public Document Target { get; } = target;
}
