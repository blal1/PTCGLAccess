namespace Accessibility;

// A building block (menu, list, readable item) owned by a component. It speaks through its parent,
// so it falls silent automatically whenever its parent loses focus.
public abstract class AccessibleElement
{
    protected readonly AccessibleComponent m_parent;

    protected AccessibleElement(AccessibleComponent parent)
    {
        m_parent = parent;
    }

    protected void Output(string text, bool interrupt = true)
    {
        AccessibilityMgr.Output(m_parent, text, interrupt);
    }
}
