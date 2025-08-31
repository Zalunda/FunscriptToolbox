using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;

namespace FunscriptToolbox.UI
{
    // FIX 1: The missing KeyboardShortcuts helper class
    public static class KeyboardShortcuts
    {
        public static void RegisterShortcuts(Window window, Dictionary<Key, Action> shortcuts)
        {
            window.PreviewKeyDown += (sender, e) =>
            {
                if (shortcuts.TryGetValue(e.Key, out var action))
                {
                    action?.Invoke();
                    e.Handled = true;
                }
            };
        }
    }
}