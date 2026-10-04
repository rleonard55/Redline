using System.Windows.Automation;
using Redline.Core.Interfaces;
using Redline.Windows.Adapters;
using Redline.Windows.Automation;

namespace Redline.Windows;

public interface ITextSurfaceAdapterFactory
{
    string Name { get; }

    /// <summary>Higher wins when several factories can handle an element.</summary>
    int Priority { get; }

    /// <summary>Called on the UIA dispatcher thread.</summary>
    bool CanHandle(ElementInfo info);

    /// <summary>Called on the UIA dispatcher thread.</summary>
    ITextSurfaceAdapter Create(UiaDispatcher uia, AutomationElement element, ElementInfo info);
}

public sealed class GenericUiaAdapterFactory : ITextSurfaceAdapterFactory
{
    private static readonly HashSet<string> WebFrameworks = new(StringComparer.OrdinalIgnoreCase) { "Chrome", "Gecko" };

    public string Name => "GenericUia";
    public int Priority => 0;

    public bool CanHandle(ElementInfo info)
    {
        if (!info.SupportsTextPattern && !info.SupportsValuePattern)
            return false;

        // Editable but never prose: Office command UI (ribbon font/size boxes, search box) and
        // the Chromium/Edge address bar (URLs and search queries).
        if (info.ClassName.StartsWith("NetUI", StringComparison.Ordinal) ||
            info.ClassName == "OmniboxViewViews")
            return false;

        switch (info.ControlType)
        {
            case "Edit":
                return true;

            case "Document":
                // A browser's page body is a read-only Document with TextPattern; analyzing it would
                // spellcheck the whole web page. Editable web content surfaces as Edit (Phase 0:
                // TinyMCE, CKEditor) or as a Document with a writable ValuePattern.
                if (WebFrameworks.Contains(info.FrameworkId))
                    return info.SupportsValuePattern && !info.ValueIsReadOnly;
                return info.SupportsTextPattern; // Word (_WwG), Notepad (RichEditD2DPT)

            case "Group":
                // A contenteditable element without an ARIA role surfaces as a Group. Chromium only
                // gives TextPattern to editable roots (plus the page Document, handled above), so a
                // focusable Chromium Group with TextPattern is an editor; a focusable card isn't.
                return info.FrameworkId == "Chrome" && info.SupportsTextPattern && info.IsKeyboardFocusable;

            default:
                return false;
        }
    }

    public ITextSurfaceAdapter Create(UiaDispatcher uia, AutomationElement element, ElementInfo info) =>
        new GenericUiaAdapter(uia, element, info);
}

/// <summary>Picks the highest-priority adapter factory that can handle an element.</summary>
public sealed class AdapterSelector
{
    private readonly IReadOnlyList<ITextSurfaceAdapterFactory> _factories;

    public AdapterSelector(IEnumerable<ITextSurfaceAdapterFactory> factories)
    {
        _factories = factories.OrderByDescending(f => f.Priority).ToList();
    }

    public ITextSurfaceAdapterFactory? Select(ElementInfo info) =>
        _factories.FirstOrDefault(f => f.CanHandle(info));
}
