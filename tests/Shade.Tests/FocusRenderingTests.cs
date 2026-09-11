using CupriFace;
using CupriFace.Accessibility;
using CupriFace.Dom;
using CupriFace.Interaction;
using SkiaSharp;

internal static class FocusRenderingTests
{
    internal static void AccessibleTextSelection()
    {
        var app = new AccessibilityStateApp();
        app.SetBindable("Value", "ab\U0001f642cd");
        app.SetBindable("Secret", "private-value");
        using var doc = app.CreateDocument();
        using (doc.RenderToImage(620, 500, SKColors.White)) { }
        RenderNode? Bound(RenderNode node, string binding) => node.Element?.GetAttribute("data-bind-value") == binding
            ? node : node.Children.Select(n => Bound(n, binding)).FirstOrDefault(n => n is not null);
        var path = PathOf(Bound(doc.Root, "Value")!);
        var secretPath = PathOf(Bound(doc.Root, "Secret")!);
        var initial = doc.GetAccessibleTextState(path)!.Value;
        Check(initial.Text == "ab\U0001f642cd" && initial.Anchor is null && initial.Caret is null, "Unfocused text snapshot invented a selection");
        Check(doc.GetAccessibleTextState(secretPath) is null, "Password entered the accessible text snapshot");
        Check(!doc.AccessibilitySelectText(secretPath, 0, 2), "Password range selection was exposed");
        Check(!doc.AccessibilitySelectText(path, -1, 2) && !doc.AccessibilitySelectText(path, 0, 20)
            && !doc.AccessibilitySelectText(path, 0, 3), "Invalid text boundary accepted");
        Check(doc.GetAccessibleTextState(path)!.Value.Caret is null, "Rejected selection stole focus");
        Check(doc.AccessibilitySelectText(path, 4, 2), "Reverse Unicode selection rejected");
        var selected = doc.GetAccessibleTextState(path)!.Value;
        Check(selected.Anchor == 4 && selected.Caret == 2, "Selection direction was lost");
        Check(doc.DispatchKey("X", EditKey.None), "Selected text replacement failed");
        Check((string?)app.GetBindable("Value") == "abXcd", "Selection replaced the wrong text");
        Check(initial.Text == "ab\U0001f642cd" && initial.Anchor is null, "Published text snapshot mutated");
        Check(doc.AccessibilitySelectText(path, 5, 5) && doc.SetComposition("z"), "Composition fixture failed");
        Check(doc.GetAccessibleTextState(path)!.Value.Text == "abXcdz" && (string?)app.GetBindable("Value") == "abXcd",
            "Accessible snapshot ignored active preedit or prematurely committed it");
        Check(doc.AccessibilitySelectText(path, 0, 2) && !doc.HasComposition && (string?)app.GetBindable("Value") == "abXcdz",
            "Accessible selection did not finish composition through normal editing");
        doc.NodeAtPath(path)!.Element!.SetAttribute("aria-readonly", "true");
        Check(doc.GetAccessibleTextState(path)!.Value.ReadOnly, "Read-only state absent");
        Check(doc.AccessibilitySelectText(path, 0, 2), "Read-only text could not be selected");
        doc.NodeAtPath(path)!.Element!.SetAttribute("aria-disabled", "true");
        Check(!doc.AccessibilitySelectText(path, 0, 1), "Disabled field selection accepted");
        Check(doc.AccessibilityFocus(secretPath), "Password fixture could not receive normal focus");
        Check(doc.GetAccessibleTextState(secretPath) is null && doc.GetAccessibleTextState(path)!.Value.Caret is null,
            "Focus change leaked password text or retained another field's caret");
    }

    internal static void AccessibilityActivation()
    {
        foreach (var overflow in new[] { "auto", "hidden", "visible" })
        foreach (var zoom in new[] { 0.5f, 1f, 2f })
        {
            using var doc = CupriDocument.Load("""
                <html><body><div id="viewport"><div id="content">
                <button id="target" role="button">Target</button>
                </div></div><button id="other" role="button">Other</button></body></html>
                """, """
                body { margin:0; }
                #viewport { position:absolute; left:20px; top:20px; width:120px; height:90px; }
                #content { height:400px; padding-top:250px; }
                button { width:60px; height:30px; padding:0; margin:0; }
                #other { position:absolute; left:20px; top:270px; }
                """ + "#viewport { overflow:" + overflow + "; }");
            var targetClicks = 0; var otherClicks = 0;
            doc.Zoom = zoom;
            doc.OnClick("#target", _ => targetClicks++);
            doc.OnClick("#other", _ => otherClicks++);
            using (doc.RenderToImage(300, 450, SKColors.White)) { }
            var path = PathOf(Find(doc.Root, "target")!);
            var activated = doc.AccessibilityActivate(path);
            Check(otherClicks == 0, "Clipped accessibility target activated another control");
            Check(overflow == "auto" ? activated && targetClicks == 1 : !activated && targetClicks == 0,
                "Accessibility activation did not respect scrolling/clipping");
        }
    }
    internal static void KeyboardScroll() => FocusScroll(false);
    internal static void AccessibilityScroll() => FocusScroll(true);
    private static void FocusScroll(bool accessibility)
    {
        using var doc = CupriDocument.Load("""
            <html><body><div id="viewport"><div id="content">
            <button id="first" role="button">First</button>
            <button id="last" role="button">Last</button>
            </div></div></body></html>
            """, """
            body { margin:0; }
            #viewport { margin:20px; width:140px; height:100px; overflow:auto; }
            #content { width:400px; height:500px; padding:8px; }
            button { display:block; width:60px; height:30px; }
            #last { margin-top:250px; margin-left:250px; }
            """);
        using (doc.RenderToImage(300, 200, SKColors.White)) { }
        foreach (var (key, id) in new[] { (EditKey.Tab, "first"), (EditKey.Tab, "last"),
            (EditKey.ShiftTab, "first"), (EditKey.ShiftTab, "last"), (EditKey.Tab, "first") })
        {
            Check(accessibility ? doc.AccessibilityFocus(PathOf(Find(doc.Root, id)!)) : doc.DispatchKey(null, key), "Focus navigation was rejected");
            using var frame = doc.RenderToImage(300, 200, SKColors.White);
            Check(FindAccessible(doc.BuildAccessibilityTree(300, 200), id)?.Focused == true, "Wrong keyboard focus target");
            var box = HitTesting.ScreenBox(Find(doc.Root, id)!);
            var viewport = HitTesting.ScreenBox(Find(doc.Root, "viewport")!);
            Check(box.X >= viewport.X && box.Y >= viewport.Y && box.X + box.W <= viewport.X + viewport.W && box.Y + box.H <= viewport.Y + viewport.H,
                "Tab navigation left focused control outside its viewport");
        }
    }
    internal static void Run()
    {
        using var doc = CupriDocument.Load("""
            <html><body><div id="viewport"><div id="content"><button id="target" role="button">Focus</button></div></div></body></html>
            """, """
            body { margin:0; background:white; }
            #viewport { margin:20px; width:120px; height:90px; overflow:auto; }
            #content { width:360px; height:400px; padding-top:100px; padding-left:80px; }
            #target { width:80px; height:30px; background:#eeeeee; color:black; }
            """);
        using (doc.RenderToImage(300, 200, SKColors.White)) { }
        var target = FindAccessible(doc.BuildAccessibilityTree(300, 200), "target")!;
        Check(doc.AccessibilityFocus(target.Path), "Could not focus fixture button");
        using (doc.RenderToImage(300, 200, SKColors.White)) { }
        var viewport = Find(doc.Root, "viewport")!;
        var path = PathOf(viewport);
        // Focus now reveals itself. Reset before exercising explicit clipping offsets.
        doc.ScrollCaptured(path, path, -1000, -1000);
        Check(doc.ScrollCaptured(path, path, 70, 50), "Fixture did not scroll");
        Verify(expectRing: true);
        Check(doc.ScrollCaptured(path, null, 45, 0), "Fixture did not partially clip target");
        Verify(expectRing: true);
        Check(doc.ScrollCaptured(path, null, 100, 0), "Fixture did not hide target");
        Verify(expectRing: false);

        void Verify(bool expectRing)
        {
            using var frame = doc.RenderToImage(300, 200, SKColors.White);
            using var pixels = SKBitmap.FromImage(frame);
            var box = HitTesting.ScreenBox(Find(doc.Root, "target")!);
            var clip = HitTesting.ScreenBox(Find(doc.Root, "viewport")!);
            var count = 0;
            for (var y = 0; y < pixels.Height; y++)
                for (var x = 0; x < pixels.Width; x++)
                {
                    if (pixels.GetPixel(x, y) != new SKColor(0x2f, 0x6f, 0xed)) continue;
                    count++;
                    Check(x >= clip.X && x < clip.X + clip.W && y >= clip.Y && y < clip.Y + clip.H,
                        "Focus outline escaped its scroll viewport");
                    Check(x >= box.X - 3 && x <= box.X + box.W + 3 && y >= box.Y - 3 && y <= box.Y + box.H + 3,
                        "Focus outline did not follow scrolled control");
                }
            Check(expectRing ? count > 10 : count == 0, "Unexpected visible focus outline");
        }
    }
    private static RenderNode? Find(RenderNode node, string id) => node.Element?.Id == id ? node : node.Children.Select(n => Find(n, id)).FirstOrDefault(n => n is not null);
    private static AccessibilityNode? FindAccessible(AccessibilityNode node, string id) => node.AutomationId == id ? node : node.Children.Select(n => FindAccessible(n, id)).FirstOrDefault(n => n is not null);
    private static string PathOf(RenderNode node)
    {
        var indices = new Stack<int>();
        for (var current = node; current.Parent is { } parent; current = parent) indices.Push(parent.Children.IndexOf(current));
        return "/" + string.Join("/", indices);
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
