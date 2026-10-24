using System.Diagnostics;
using System.Text.Json;
using Sunder.App.Services;
using Sunder.Core.TestSupport;
using Xunit;

namespace Sunder.App.Tests;

public sealed class AppWebDevelopmentScriptTests
{
    // Executes the actual generated scripts against a deterministic DOM boundary,
    // not a browser/native-capture qualification or a page-authored script fixture.
    [Fact]
    public async Task FixedDocumentScriptsBoundAndRedactInspectionAndRejectStaleOrProtectedInput()
    {
        using var temporary = TemporaryDirectoryScope.Create("development-document-script");
        var snapshot = new string('a', 32);
        const string text = "'); globalThis.arbitraryScript = true; //\n😀";
        var configuration = new
        {
            inspect = AppWebDevelopmentScript.Generate(new("owned-key", "owned-document", "inspect", snapshot)),
            invoke = AppWebDevelopmentScript.Generate(new("owned-key", "owned-document", "input", snapshot, new string('0', 31) + "1", "invoke")),
            setText = AppWebDevelopmentScript.Generate(new("owned-key", "owned-document", "input", snapshot, new string('0', 31) + "2", "set-text", text)),
            protectedInput = AppWebDevelopmentScript.Generate(new("owned-key", "owned-document", "input", snapshot, new string('0', 31) + "3", "set-text", "replacement")),
            wrongDocument = AppWebDevelopmentScript.Generate(new("owned-key", "replacement-document", "input", snapshot, new string('0', 31) + "1", "invoke")),
            text,
        };
        var configurationPath = temporary.GetPath("scripts.json");
        var harnessPath = temporary.GetPath("oracle.mjs");
        await File.WriteAllTextAsync(configurationPath, JsonSerializer.Serialize(configuration));
        await File.WriteAllTextAsync(harnessPath, Oracle);
        var start = new ProcessStartInfo("node") { WorkingDirectory = temporary.RootPath, UseShellExecute = false };
        start.ArgumentList.Add(harnessPath); start.ArgumentList.Add(configurationPath);
        await using var process = CapturedProcess.Start(start);
        var result = await process.EnsureSuccessAsync(TimeSpan.FromSeconds(20));
        Assert.Equal("document-script-oracle-passed", result.StandardOutput.Trim());
    }

    private const string Oracle = """
        import { readFile } from 'node:fs/promises';
        import assert from 'node:assert/strict';
        import vm from 'node:vm';
        const scripts = JSON.parse(await readFile(process.argv[2], 'utf8'));
        let document, ticks = 0, identity = 0;
        class Text {
          constructor(value) { this.nodeType = 3; this.value = value; }
          substringData(start, count) { return this.value.substring(start, start + count); }
        }
        class Element {
          constructor(tag, text = '', attributes = {}) {
            this.nodeType = 1; this.tagName = tag; this.attributes = attributes; this.childNodes = text ? [new Text(text)] : [];
            this.parentElement = null; this.ownerDocument = document; this.isConnected = true;
            this.hidden = false; this.inert = false; this.isContentEditable = false; this.readOnly = false; this.disabled = false;
            this.scrollHeight = 0; this.clientHeight = 20; this.clicks = 0; this.events = []; this.style = {};
            this.bounds = {width:100,height:20,top:0,left:0,right:100,bottom:20};
          }
          append(child) { this.childNodes.push(child); if (child.nodeType === 1) { child.parentElement = this; child.ownerDocument = document; } return child; }
          getAttribute(name) { return this.attributes[name] ?? null; }
          hasAttribute(name) { return Object.hasOwn(this.attributes, name); }
          matches(selector) { assert.equal(selector, ':disabled'); return this.disabled; }
          getBoundingClientRect() { return this.bounds; }
          click() { this.clicks++; }
          dispatchEvent(event) { this.events.push(event.type); }
          scrollBy(options) { this.scrollTop = (this.scrollTop || 0) + options.top; }
        }
        class HTMLInputElement extends Element {
          constructor(type = 'text', attributes = {}) { super('INPUT', '', attributes); this.type = type; this._value = 'private-current-input'; }
          get value() { throw new Error('Inspection must not read input values.'); }
          set value(value) { this._value = value; }
        }
        class HTMLTextAreaElement extends Element {
          constructor() { super('TEXTAREA', 'private-default-input'); this._value = 'private-current-textarea'; }
          get value() { throw new Error('Inspection must not read textarea values.'); }
          set value(value) { this._value = value; }
        }
        class HTMLSelectElement extends Element {}
        class HTMLButtonElement extends Element { constructor(text, attrs) { super('BUTTON', text, attrs); } }
        class HTMLAnchorElement extends Element {}
        function elements(root) {
          const result = [], pending = [root];
          while (pending.length) {
            const element = pending.pop(); result.push(element);
            for (let i = element.childNodes.length - 1; i >= 0; i--) if (element.childNodes[i].nodeType === 1) pending.push(element.childNodes[i]);
          }
          return result;
        }
        document = {
          createTreeWalker(root) { const nodes = elements(root); let index = 0; return {nextNode:() => nodes[++index] || null}; },
          getElementById(id) { return elements(this.body).find(element => element.getAttribute('id') === id) || null; },
        };
        const body = document.body = new Element('BODY');
        const button = body.append(new HTMLButtonElement('Choose action'));
        const input = body.append(new HTMLInputElement('text', {'aria-label':'Public input label'}));
        const password = body.append(new HTMLInputElement('password', {'aria-label':'private-password-label'}));
        const label = body.append(new Element('LABEL', 'Public textarea label'));
        const textarea = label.append(new HTMLTextAreaElement()); textarea.labels = [label];
        const hidden = body.append(new HTMLButtonElement('private-hidden')); hidden.hidden = true;
        const offscreen = body.append(new HTMLButtonElement('private-offscreen')); offscreen.bounds.top = 900;
        const iframe = body.append(new Element('IFRAME')); iframe.append(new HTMLButtonElement('private-other-document'));
        const disabled = body.append(new HTMLButtonElement('Disabled action')); disabled.disabled = true;
        const sandbox = {document, window:{}, HTMLInputElement, HTMLTextAreaElement, HTMLSelectElement, HTMLButtonElement, HTMLAnchorElement,
          NodeFilter:{SHOW_ELEMENT:1}, TextEncoder, Event:class {constructor(type) {this.type=type;}},
          innerHeight:600, innerWidth:800, performance:{now:() => ticks}, crypto:{randomUUID:() => (++identity).toString(16).padStart(32,'0')},
          getComputedStyle:element => ({display:'block',visibility:'visible',opacity:'1',overflowY:'visible',...element.style}),
        };
        const context = vm.createContext(sandbox);
        const run = script => JSON.parse(vm.runInContext(script, context, {timeout:1000}));
        const first = run(scripts.inspect);
        assert.equal(first.state, 'inspected'); assert.equal(first.wasTruncated, true);
        assert(!JSON.stringify(first).includes('private-'));
        assert.equal(first.elements.find(element => element.name === 'Choose action').reference, '0'.repeat(31)+'1');
        assert.deepEqual(first.elements.find(element => element.name === 'Protected input').actions, []);
        assert.deepEqual(first.elements.find(element => element.name === 'Disabled action').actions, []);
        assert.equal(run(scripts.setText).state, 'dispatched');
        assert.equal(input._value, scripts.text); assert.deepEqual(input.events, ['input','change']);
        assert.equal(sandbox.arbitraryScript, undefined);
        assert.equal(run(scripts.protectedInput).state, 'rejected'); assert.equal(password._value, 'private-current-input');
        assert.equal(run(scripts.invoke).state, 'dispatched'); assert.equal(button.clicks, 1);
        button.disabled = true; assert.equal(run(scripts.invoke).state, 'rejected'); assert.equal(button.clicks, 1); button.disabled = false;
        assert.equal(run(scripts.wrongDocument).state, 'rejected');
        ticks = 30000; assert.equal(run(scripts.invoke).state, 'rejected'); assert.equal(button.clicks, 1);
        ticks = 0;
        // Changing type after inspection must revoke the old set-text authority.
        input.type = 'password'; assert.equal(run(scripts.setText).state, 'rejected'); input.type = 'text';
        input.ownerDocument = {}; assert.equal(run(scripts.setText).state, 'rejected'); input.ownerDocument = document;
        input.isConnected = false; assert.equal(run(scripts.setText).state, 'rejected'); input.isConnected = true;
        // New documents cannot reuse the original map even if the old global were retained.
        context.document = {}; assert.equal(run(scripts.invoke).state, 'rejected'); context.document = document;
        body.childNodes = [];
        for (let i = 0; i < 600; i++) body.append(new HTMLButtonElement('😀'.repeat(300)));
        const bounded = run(scripts.inspect);
        assert.equal(bounded.state, 'inspected'); assert.equal(bounded.wasTruncated, true);
        assert(bounded.elements.length <= 128); assert(Buffer.byteLength(JSON.stringify(bounded)) < 64*1024);
        assert(bounded.elements.every(element => Array.from(element.name).length <= 128 && !/[\uD800-\uDBFF]$/.test(element.name)));
        console.log('document-script-oracle-passed');
        """;
}
