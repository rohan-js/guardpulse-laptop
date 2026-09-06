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

    /// <summary>Finds the per-tab close button. Fallback order, each documented:
    /// 1. "Close tab" name match (Chromium variants, English UI);
    /// 2. AutomationId containing "close" (case-insensitive) — covers Firefox
    ///    and localized Chromium strips where the accessible name is translated
    ///    but the automation id keeps the English token;
    /// 3. the LAST button descendant — Chromium renders the close affordance at
    ///    the tab's trailing edge, so with >=1 button the final one is the
    ///    close button (single-button strips keep the old behavior as a subset).</summary>
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

        AutomationElementCollection? buttons = null;
        try
        {
            buttons = tab.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
        }
        catch (ElementNotAvailableException)
        {
        }

        if (buttons is null || buttons.Count == 0) return null;

        // Fallback 2: automation id keeps the English "close" token on
        // Firefox/localized strips where the accessible name is translated.
        foreach (AutomationElement button in buttons)
        {
            string? automationId;
            try
            {
                automationId = button.Current.AutomationId;
            }
            catch (ElementNotAvailableException)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(automationId)
                && automationId.Contains("close", StringComparison.OrdinalIgnoreCase))
            {
                return button;
            }
        }

        // Fallback 3: Chromium puts the close button at the tab's trailing edge,
        // so the last button descendant is the close affordance.
        return buttons[buttons.Count - 1];
    }
}
