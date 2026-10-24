using System.Text.Json;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Media;
using Sunder.App.Services;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.App.Tests;

// The natural control/provider boundary is exercised without an OS window. The
// production Host binding separately owns attached-view and activation authority.
public sealed class AppDevelopmentViewOperationsTests
{
    [Fact]
    public async Task InspectionAndInputStayInsideTheSelectedViewAndDoNotReadInputValues()
    {
        var fixture = new Scene();
        var snapshot = await fixture.InspectAsync();
        var element = Assert.Single(snapshot.Elements);
        Assert.Equal("Selected action", element.Name);
        Assert.DoesNotContain("outside", JsonSerializer.Serialize(snapshot), StringComparison.Ordinal);
        Assert.DoesNotContain("private-value", JsonSerializer.Serialize(snapshot), StringComparison.Ordinal);
        var request = Input(snapshot, element, "set-text", "new text");
        Assert.Equal("dispatched", (await fixture.Operations.InteractAsync(request, default)).State);
        Assert.Equal("new text", fixture.Selected.Value);
        Assert.Equal(1, fixture.Selected.Inputs);
        Assert.Equal("private-value", fixture.Outside.Value);
        Assert.Equal("dispatched", (await fixture.Operations.InteractAsync(request, default)).State);
        Assert.Equal(1, fixture.Selected.Inputs);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Operations.InteractAsync(request with { Text = "changed retry" }, default));
        Assert.Equal(1, fixture.Selected.Inputs);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("detached")]
    [InlineData("disabled")]
    [InlineData("hidden")]
    [InlineData("activation")]
    public async Task StaleOrInaccessibleElementsCannotDispatch(string boundary)
    {
        var fixture = new Scene();
        var snapshot = await fixture.InspectAsync();
        var request = Input(snapshot, Assert.Single(snapshot.Elements), "invoke");
        switch (boundary)
        {
            case "expired": fixture.Clock.Ticks += TimeSpan.FromSeconds(30).Ticks; break;
            case "detached": fixture.Root.Children.Remove(fixture.Selected); fixture.OtherRoot.Children.Add(fixture.Selected); break;
            case "disabled": fixture.Selected.IsEnabled = false; break;
            case "hidden": fixture.Selected.IsVisible = false; break;
            case "activation": fixture.IsCurrent = false; break;
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Operations.InteractAsync(request, default));
        Assert.Equal(0, fixture.Selected.Inputs);
    }

    [Fact]
    public async Task InvalidArgumentsAndForeignReferencesAreRejectedBeforeAdmission()
    {
        var fixture = new Scene();
        var snapshot = await fixture.InspectAsync();
        var request = Input(snapshot, Assert.Single(snapshot.Elements), "invoke");
        foreach (var invalid in new[] { request with { Action = "evaluate-script" }, request with { Text = "extra" },
                     request with { OperationId = "not-an-id" }, request with { ElementReference = Guid.NewGuid().ToString("N") },
                     request with { Action = "set-text", Text = new string('x', 4097) } })
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Operations.InteractAsync(invalid, default));
        Assert.Equal(0, fixture.Selected.Inputs);
        Assert.False((await fixture.Operations.InteractAsync(request, default)).OutcomeUnknown);
        Assert.Equal(1, fixture.Selected.Inputs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnExceptionOrRetirementAfterDispatchRetainsAnUnknownReceiptWithoutReplay(bool retire)
    {
        var fixture = new Scene();
        var snapshot = await fixture.InspectAsync();
        fixture.Selected.AfterInput = () =>
        {
            if (retire) fixture.IsCurrent = false;
            else throw new InvalidOperationException("Effect happened before the handler failed.");
        };
        var request = Input(snapshot, Assert.Single(snapshot.Elements), "invoke");
        var result = await fixture.Operations.InteractAsync(request, default);
        Assert.True(result.OutcomeUnknown);
        Assert.Equal("unknown", result.State);
        Assert.Equal(result, await fixture.Operations.InteractAsync(request, default));
        Assert.Equal(1, fixture.Selected.Inputs);
    }

    [Fact]
    public async Task MutationReceiptsSurviveSnapshotEvictionAndCapacityRejectsInsteadOfForgettingThem()
    {
        var fixture = new Scene();
        var snapshot = await fixture.InspectAsync();
        var original = Input(snapshot, Assert.Single(snapshot.Elements), "invoke");
        for (var index = 0; index < 64; index++)
            await fixture.Operations.InteractAsync(index == 0 ? original : original with { OperationId = Guid.NewGuid().ToString("N") }, default);
        for (var index = 0; index < 9; index++) await fixture.InspectAsync();
        Assert.Equal("dispatched", (await fixture.Operations.InteractAsync(original, default)).State);
        var fresh = await fixture.InspectAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Operations.InteractAsync(Input(fresh, Assert.Single(fresh.Elements), "invoke"), default));
        Assert.Equal(64, fixture.Selected.Inputs);
    }

    [Fact]
    public async Task PreCancelledInputDoesNotConsumeItsIdentityOrDispatchLater()
    {
        var fixture = new Scene();
        var snapshot = await fixture.InspectAsync();
        var request = Input(snapshot, Assert.Single(snapshot.Elements), "invoke");
        using var stopped = new CancellationTokenSource(); stopped.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Operations.InteractAsync(request, stopped.Token));
        Assert.Equal(0, fixture.Selected.Inputs);
        await fixture.Operations.InteractAsync(request, default);
        Assert.Equal(1, fixture.Selected.Inputs);
    }

    [Fact]
    public async Task CancellationDuringDispatchRetainsAnUnknownReceiptInsteadOfClaimingConfirmation()
    {
        var fixture = new Scene();
        var snapshot = await fixture.InspectAsync();
        using var stopped = new CancellationTokenSource();
        fixture.Selected.AfterInput = stopped.Cancel;
        var request = Input(snapshot, Assert.Single(snapshot.Elements), "invoke");
        var result = await fixture.Operations.InteractAsync(request, stopped.Token);
        Assert.True(result.OutcomeUnknown);
        Assert.Equal("unknown", result.State);
        Assert.Equal(result, await fixture.Operations.InteractAsync(request, default));
        Assert.Equal(1, fixture.Selected.Inputs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InputTemplateDescendantsNeverExposeValuesOrInheritElementAuthority(bool password)
    {
        var fixture = new Scene();
        var snapshot = await fixture.InspectAsync();
        var request = Input(snapshot, Assert.Single(snapshot.Elements), "invoke");
        fixture.Root.Children.Remove(fixture.Selected);
        var box = new InputWithVisualChild { Width = 150, Height = 30, PasswordChar = password ? '*' : '\0' };
        fixture.Selected.Label = "private-input-template-text";
        box.AddVisual(fixture.Selected);
        fixture.Root.Children.Add(box);
        fixture.Layout();
        fixture.Selected.Arrange(new(0, 0, 80, 20));
        var inspected = await fixture.InspectAsync();
        Assert.DoesNotContain("private-input-template-text", JsonSerializer.Serialize(inspected), StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Operations.InteractAsync(request, default));
        Assert.Equal(0, fixture.Selected.Inputs);
    }

    [Fact]
    public async Task InspectionBoundsEscapedUnicodeAndNeverReturnsPasswordContentsOrInputActions()
    {
        var fixture = new Scene();
        fixture.Root.Children.Clear();
        var password = new TextBox { Width = 80, Height = 20, PasswordChar = '*', Text = "private-password" };
        Avalonia.Automation.AutomationProperties.SetName(password, "private-password-label");
        fixture.Root.Children.Add(password);
        for (var index = 0; index < 300; index++) fixture.Root.Children.Add(new ActionControl { Label = string.Concat(Enumerable.Repeat("\U0001F600", 200)) });
        fixture.Layout();
        var snapshot = await fixture.InspectAsync();
        Assert.True(snapshot.WasTruncated);
        Assert.InRange(snapshot.Elements.Count, 1, 128);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(snapshot).Length < 64 * 1024);
        Assert.DoesNotContain("private-password", JsonSerializer.Serialize(snapshot), StringComparison.Ordinal);
        var protectedInput = Assert.Single(snapshot.Elements, element => element.Name == "Protected input");
        Assert.Empty(protectedInput.Actions);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Operations.InteractAsync(Input(snapshot, protectedInput, "set-text", "replacement"), default));
        Assert.Equal("private-password", password.Text);
    }

    [Theory]
    [InlineData("current")]
    [InlineData("before")]
    [InlineData("during")]
    public async Task NativeDocumentElementsAreOpaqueAndNeverFollowANewDocumentOrReplayPendingInput(string navigation)
    {
        var fixture = new Scene();
        fixture.Root.Children.Clear();
        var browser = new DocumentControl();
        fixture.Root.Children.Add(browser); fixture.Layout();
        var snapshot = await fixture.InspectAsync();
        var element = Assert.Single(snapshot.Elements);
        Assert.Equal("Native document action", element.Name);
        Assert.NotEqual(browser.Source.NativeReference, element.Reference);
        Assert.DoesNotContain("native-wrapper-is-not-document-content", JsonSerializer.Serialize(snapshot), StringComparison.Ordinal);
        var request = Input(snapshot, element, "invoke");
        if (navigation == "before")
        {
            browser.Source.Current = false;
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Operations.InteractAsync(request, default));
            Assert.Equal(0, browser.Source.Inputs);
            return;
        }
        var pending = fixture.Operations.InteractAsync(request, default);
        Assert.False(pending.IsCompleted);
        var interim = await fixture.Operations.InteractAsync(request, default);
        Assert.True(interim.OutcomeUnknown); // Observes the admission, never another native evaluation.
        if (navigation == "during") browser.Source.Current = false;
        browser.Source.Reply.SetResult(true);
        var result = await pending;
        Assert.Equal(navigation == "during", result.OutcomeUnknown);
        Assert.Equal(navigation == "during" ? "unknown" : "dispatched", result.State);
        Assert.Equal(result, await fixture.Operations.InteractAsync(request, default));
        Assert.Equal(1, browser.Source.Inputs);
    }

    [Fact]
    public async Task SemanticQueryFiltersBeforeSelectingAndPagesOnlyAnUnchangedExactView()
    {
        var fixture = new Scene();
        fixture.Root.Children.Clear();
        for (var index = 0; index < 180; index++) fixture.Root.Children.Add(new ActionControl { Label = index == 170 ? "Wanted action" : "Other action " + index });
        fixture.Layout();
        var binding = new AppDevelopmentViewBinding("test.development", "test.view", new(Guid.NewGuid(), 1, new('a', 64)), fixture.Root, new ImmediateDispatcher(), () => { });
        var found = await fixture.Operations.QueryAsync(binding, new() { Name = "Wanted" }, default);
        Assert.Equal("Wanted action", Assert.Single(found.Snapshot.Elements).Name);
        Assert.Null(found.NextCursor);
        var page = await fixture.Operations.QueryAsync(binding, new() { Name = "Other", Limit = 3 }, default);
        Assert.Equal(3, page.Snapshot.Elements.Count);
        Assert.NotNull(page.NextCursor);
        var next = await fixture.Operations.QueryAsync(binding, new() { Name = "Other", Limit = 3, Cursor = page.NextCursor }, default);
        Assert.DoesNotContain(next.Snapshot.Elements, item => page.Snapshot.Elements.Any(previous => previous.Reference == item.Reference));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Operations.QueryAsync(binding, new() { Name = "changed", Cursor = page.NextCursor }, default));
        fixture.Clock.Ticks += TimeSpan.FromSeconds(30).Ticks;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Operations.QueryAsync(binding, new() { Name = "Other", Cursor = page.NextCursor }, default));
    }

    [Fact]
    public async Task ElementStateRequiresAnOriginalReferenceAndValueIsOptIn()
    {
        var fixture = new Scene();
        var binding = new AppDevelopmentViewBinding("test.development", "test.view", new(Guid.NewGuid(), 1, new('a', 64)), fixture.Root, new ImmediateDispatcher(), () => { });
        var page = await fixture.Operations.QueryAsync(binding, new(), default);
        var element = Assert.Single(page.Snapshot.Elements);
        Assert.Empty(element.State);
        var query = new PackageDevelopmentViewQuery { SnapshotId = page.Snapshot.SnapshotId, ElementReference = element.Reference, IncludeValue = true };
        var detail = await fixture.Operations.QueryAsync(binding, query, default);
        Assert.Equal("private-value", Assert.Single(detail.Snapshot.Elements).State["value"]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Operations.QueryAsync(binding with { Root = fixture.OtherRoot }, query, default));
        fixture.Root.Children.Remove(fixture.Selected);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Operations.QueryAsync(binding, query, default));
    }

    [Fact]
    public async Task ViewPageCannotOutliveItsActionableSnapshotOrOriginalNativeDocument()
    {
        var fixture = new Scene();
        fixture.Root.Children.Add(new ActionControl()); fixture.Layout();
        var binding = new AppDevelopmentViewBinding("test.development", "test.view", new(Guid.NewGuid(), 1, new('a', 64)), fixture.Root, new ImmediateDispatcher(), () => { });
        var page = await fixture.Operations.QueryAsync(binding, new() { Limit = 1 }, default);
        Assert.NotNull(page.NextCursor);
        for (var index = 0; index < 8; index++) await fixture.Operations.QueryAsync(binding, new(), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Operations.QueryAsync(binding, new() { Limit = 1, Cursor = page.NextCursor }, default));
        fixture.Root.Children.Clear();
        var browser = new DocumentControl();
        fixture.Root.Children.Add(browser); fixture.Root.Children.Add(new ActionControl()); fixture.Layout();
        var nativePage = await fixture.Operations.QueryAsync(binding, new() { Limit = 1 }, default);
        Assert.NotNull(nativePage.NextCursor);
        browser.Source.Current = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Operations.QueryAsync(binding, new() { Limit = 1, Cursor = nativePage.NextCursor }, default));
    }

    [Theory]
    [InlineData("surface", "opacity")]
    [InlineData("surface", "mask")]
    [InlineData("surface", "clip")]
    [InlineData("surface", "effect")]
    [InlineData("ancestor", "opacity")]
    [InlineData("ancestor", "mask")]
    [InlineData("ancestor", "clip")]
    [InlineData("ancestor", "effect")]
    public async Task NativeCaptureRejectsUnprovableEffectsEvenWithoutClipToBounds(string boundary, string kind)
    {
        var fixture = new Scene();
        fixture.Root.Children.Clear();
        var browser = new DocumentControl();
        fixture.Root.Children.Add(browser); fixture.Layout();
        var affected = boundary == "surface" ? (Control)browser : fixture.Root;
        Assert.False(affected.ClipToBounds);
        switch (kind)
        {
            case "opacity": affected.Opacity = .5; break;
            case "mask": affected.OpacityMask = Brushes.Black; break;
            case "clip": affected.Clip = new RectangleGeometry(new Rect(0, 0, 20, 20)); break;
            case "effect": affected.Effect = new BlurEffect { Radius = 2 }; break;
        }
        var binding = new AppDevelopmentViewBinding("test.development", "test.view", new(Guid.NewGuid(), 1, new('a', 64)),
            fixture.Root, new ImmediateDispatcher(), () => { });
        var error = await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Operations.CaptureAsync(binding, new(), 1280, default));
        Assert.Contains("clip/effect composition", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, browser.Source.Captures);
    }

    private static PackageDevelopmentViewInput Input(PackageDevelopmentViewSnapshot snapshot, PackageDevelopmentViewElement element, string action, string? text = null)
        => new(Guid.NewGuid().ToString("N"), snapshot.SnapshotId, element.Reference, action, text);

    private sealed class Scene
    {
        internal readonly Clock Clock = new();
        internal readonly AppDevelopmentViewOperations Operations;
        internal readonly Panel Root = new();
        internal readonly Panel OtherRoot = new();
        internal readonly ActionControl Selected = new() { Label = "Selected action" };
        internal readonly ActionControl Outside = new() { Label = "outside action" };
        internal bool IsCurrent = true;
        internal Scene()
        {
            Operations = new(Clock); Root.Children.Add(Selected); OtherRoot.Children.Add(Outside); Layout();
        }
        internal void Layout() { Root.Measure(new(300, 200)); Root.Arrange(new(0, 0, 300, 200)); OtherRoot.Measure(new(300, 200)); OtherRoot.Arrange(new(0, 0, 300, 200)); }
        internal Task<PackageDevelopmentViewSnapshot> InspectAsync() => Operations.InspectAsync(new("test.development", "test.view",
            new(Guid.NewGuid(), 1, new string('a', 64)), Root, new ImmediateDispatcher(), () =>
            { if (!IsCurrent) throw new InvalidOperationException("The exact activation retired."); }), default);
    }

    private sealed class ActionControl : Control
    {
        internal string Label = "Action";
        internal string Value = "private-value";
        internal int Inputs;
        internal Action? AfterInput;
        internal ActionControl() { Width = 80; Height = 20; }
        protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);
        private sealed class Peer(ActionControl owner) : ControlAutomationPeer(owner), IInvokeProvider, IValueProvider
        {
            protected override string GetNameCore() => owner.Label;
            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;
            public bool IsReadOnly => false;
            public string Value => owner.Value;
            public void SetValue(string? value) { owner.Value = value ?? string.Empty; Invoke(); }
            public void Invoke() { owner.Inputs++; owner.AfterInput?.Invoke(); }
        }
    }
    private sealed class InputWithVisualChild : TextBox
    {
        internal void AddVisual(Control child) => VisualChildren.Add(child);
    }
    private sealed class DocumentControl : Decorator, IAppDevelopmentDocumentOwner
    {
        internal readonly DocumentSource Source = new();
        public IAppDevelopmentDocument DevelopmentDocument => Source;
        internal DocumentControl() { Width = 250; Height = 150; Child = new ActionControl { Label = "native-wrapper-is-not-document-content" }; }
    }
    private sealed class DocumentSource : IAppDevelopmentDocument
    {
        internal readonly string NativeReference = Guid.NewGuid().ToString("N");
        internal readonly TaskCompletionSource<bool> Reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Inputs;
        internal int Captures;
        internal bool Current = true;
        public void RequireCurrent(string documentId) { if (!Current || documentId != "original-document") throw new InvalidOperationException("Document changed."); }
        public Task<AppDevelopmentDocumentSnapshot> InspectAsync(string snapshotId, CancellationToken token)
            => Task.FromResult(new AppDevelopmentDocumentSnapshot("original-document", [new(NativeReference, "button", "Native document action", true, ["invoke"])], false));
        public Task<bool> InteractAsync(string documentId, PackageDevelopmentViewInput request, string nativeElementReference, CancellationToken token)
        {
            Assert.Equal(NativeReference, nativeElementReference); Inputs++; return Reply.Task;
        }
        public Task<(byte[] Png, Rect Bounds)> CaptureAsync(string? snapshotId, string? element, int edge, CancellationToken token)
        {
            Captures++;
            throw new InvalidOperationException("Unprovable native composition must reject before taking pixels.");
        }
    }
    private sealed class Clock : TimeProvider
    {
        internal long Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
    }
    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public Task InvokeAsync(Func<Task> action) => action();
        public Task<T> InvokeAsync<T>(Func<T> action) => Task.FromResult(action());
        public Task<T> InvokeAsync<T>(Func<Task<T>> action) => action();
    }
}
