namespace Egap.Documents.Layout;

/// <summary>
/// Told what <see cref="PageLayout.Draw(Egami.Drawing.Canvas, LayoutDrawObserver?)"/> is drawing, so the target canvas can
/// group its output: PDF export marks each line and picture with its structure element and the rest as artifacts
/// (Phase 6 Build Plan task 22). Every Begin is matched by its End, and calls nest as the boxes do.
/// </summary>
public abstract class LayoutDrawObserver
{
    /// <summary>The drawing of <paramref name="box"/> and its children starts.</summary>
    public virtual void BeginBox(LayoutBox box)
    {
    }

    /// <summary>The drawing of <paramref name="box"/> and its children has ended.</summary>
    public virtual void EndBox(LayoutBox box)
    {
    }

    /// <summary>
    /// Drawing that is not content starts: paragraph shading and borders, cell fills and borders, a text box's frame, the
    /// note separator. Never inside a line's drawing.
    /// </summary>
    public virtual void BeginDecoration()
    {
    }

    /// <summary>The decoration started by <see cref="BeginDecoration"/> has ended.</summary>
    public virtual void EndDecoration()
    {
    }

    /// <summary>
    /// Inside a line's drawing, a ruby's base (or, when <paramref name="annotation"/>, its annotation) starts; ends with
    /// <see cref="EndLinePart"/>.
    /// </summary>
    internal virtual void BeginRuby(Ruby ruby, bool annotation)
    {
    }

    /// <summary>Inside a line's drawing, emphasis marks, which are no text, start; ends with <see cref="EndLinePart"/>.</summary>
    internal virtual void BeginMarks()
    {
    }

    /// <summary>The part of a line <see cref="BeginRuby"/> or <see cref="BeginMarks"/> started has ended.</summary>
    internal virtual void EndLinePart()
    {
    }
}
