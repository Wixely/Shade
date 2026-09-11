Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, WindowsBase
Add-Type -ReferencedAssemblies @([System.Windows.Automation.AutomationElement].Assembly.Location, [System.Windows.Automation.AutomationProperty].Assembly.Location, [System.Windows.Rect].Assembly.Location) -TypeDefinition @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Windows.Automation;
public static class ShadeAutomationEvents {
    private static readonly List<AutomationPropertyChangedEventArgs> events = new List<AutomationPropertyChangedEventArgs>();
    private static readonly AutomationPropertyChangedEventHandler handler = OnChange;
    private static AutomationElement element;
    private static void OnChange(object sender, AutomationPropertyChangedEventArgs change) { lock(events) { events.Add(change); } }
    public static void Start(AutomationElement target) {
        lock(events) { events.Clear(); }
        element = target;
        Automation.AddAutomationPropertyChangedEventHandler(target, TreeScope.Element, handler,
            ValuePattern.ValueProperty, ValuePattern.IsReadOnlyProperty, AutomationElement.IsEnabledProperty,
            RangeValuePattern.ValueProperty, TogglePattern.ToggleStateProperty, AutomationElement.NameProperty);
    }
    public static bool Seen(int property, object value) {
        lock(events) { foreach(var change in events) if(change.Property.Id == property && Object.Equals(change.NewValue, value)) return true; }
        return false;
    }
    public static string Describe() { lock(events) { var text = new StringBuilder(); foreach(var e in events) text.Append(e.Property.Id).Append(':').Append(e.NewValue == null ? "null" : e.NewValue.GetType().Name).Append(' '); return text.ToString(); } }
    public static bool AnyValueEvent() { lock(events) { foreach(var e in events) if(e.Property == ValuePattern.ValueProperty) return true; return false; } }
    public static void Stop() { var target = element; element = null; if(target != null) Automation.RemoveAutomationPropertyChangedEventHandler(target, handler); }
}
'@
