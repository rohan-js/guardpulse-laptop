namespace GuardPulse.Agent.Session;

using System.Windows.Automation;

/// <summary>
/// Closes the SELECTED tab of a browser window through UI Automation: find the tab
/// strip's selected TabItem, locate its per-tab "Close tab" button child, invoke it.
/// Pure UIA — no keystrokes, no pointer events, no focus change, no process kills.
/// </summary>
internal static class TabEnforcer
{
    /// <summary>Attempts a UIA close of the selected tab on the given browser window.
    /// Returns true when the invoke was delivered.</summary>
    public static bool CloseSelectedTab(nint browserHwnd)
    {
        if (browserHwnd == nint.Zero) return false;
        try
        {
            var root = AutomationElement.FromHandle(browserHwnd);
            var tabs = root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));
            if (tabs is null || tabs.Count == 0) return false;

            foreach (AutomationElement tab in tabs)
            {
                bool selected;
                try
                {
                    var selection = tab.GetCurrentPattern(SelectionItemPattern.Pattern) as SelectionItemPattern;
                    selected = selection?.Current.IsSelected == true;
                }
                catch (InvalidOperationException)
                {
                    continue; // not a real tab item (e.g. list-item stand-in)
                }

                if (!selected) continue;

                var closeBtn = FindCloseButton(tab);
                if (closeBtn is null) return false;

                if (closeBtn.GetCurrentPattern(InvokePattern.Pattern) is InvokePattern invoke)
                {
                    invoke.Invoke();
                    return true;
                }

                return false;
            }

            return false;
        }
        catch (ElementNotAvailableException)
        {
            return false; // window/tab vanished mid-walk
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Finds the per-tab close button ("Close tab" name on Chromium variants),
    /// falling back to the tab's only button descendant.</summary>
    private static AutomationElement? FindCloseButton(AutomationElement tab)
    {
        try
        {
            var named = tab.FindFirst(TreeScope.Descendants,
                new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                    new PropertyCondition(AutomationElement.NameProperty, "Close tab")));
            if (named is not null) return named;
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }

        try
        {
            var buttons = tab.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            if (buttons is not null && buttons.Count == 1)
            {
                return buttons[0];
            }
        }
        catch (ElementNotAvailableException)
        {
        }

        return null;
    }
}
