using System.Text.Json;

namespace Sunder.App.Services;

internal sealed record AppWebDevelopmentRequest(string Key, string DocumentId, string Command, string SnapshotId,
    string? ElementReference = null, string? Action = null, string? Text = null, Sunder.Sdk.Abstractions.PackageDevelopmentViewQuery? Query = null);

internal static class AppWebDevelopmentScript
{
    internal static string Generate(AppWebDevelopmentRequest request)
        => "(" + Script + ")(" + JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web)) + ")";

    // This is a bounded top-level-document adapter, not a remotely programmable
    // browser API. Page content/labels remain untrusted even inside this closure.
    private const string Script = """
        function (request) {
          'use strict';
          const reply = (state, elements, wasTruncated) => JSON.stringify({state, ...(elements ? {elements, wasTruncated} : {})});
          const id = value => typeof value === 'string' && /^[a-f0-9]{32}$/.test(value);
          if (!id(request.snapshotId)) return reply('rejected');
          const field = element => element instanceof HTMLInputElement || element instanceof HTMLTextAreaElement || element instanceof HTMLSelectElement || element.isContentEditable;
          const privateNode = element => {
            let parent = element;
            for (let depth = 0; parent && depth < 64; depth++, parent = parent.parentElement) {
              if (parent.hidden || parent.inert || parent.getAttribute('aria-hidden') === 'true'
                  || ['SCRIPT','STYLE','IFRAME','OBJECT','EMBED'].includes(parent.tagName)) return true;
              if (parent !== element && field(parent)) return true;
            }
            return parent !== null;
          };
          const visible = element => {
            if (!element.isConnected || element.ownerDocument !== document || privateNode(element)) return false;
            const style = getComputedStyle(element), bounds = element.getBoundingClientRect();
            return style.display !== 'none' && style.visibility === 'visible' && style.opacity !== '0'
              && bounds.width > 0 && bounds.height > 0 && bounds.bottom > 0 && bounds.right > 0
              && bounds.top < innerHeight && bounds.left < innerWidth;
          };
          const enabled = element => !element.matches(':disabled') && element.getAttribute('aria-disabled') !== 'true';
          const protectedInput = element => element instanceof HTMLInputElement && element.type === 'password';
          const actions = element => {
            if (!visible(element) || !enabled(element) || protectedInput(element)) return [];
            const result = [], role = element.getAttribute('role');
            if (element instanceof HTMLButtonElement || element instanceof HTMLAnchorElement || role === 'button' || role === 'link') result.push('invoke');
            if (!element.readOnly && (element instanceof HTMLTextAreaElement || element instanceof HTMLInputElement
                && ['text','search','email','url','tel','number'].includes(element.type))) result.push('set-text');
            if (element instanceof HTMLInputElement && element.type === 'checkbox' || role === 'checkbox') result.push('toggle');
            if (element.scrollHeight > element.clientHeight && element.clientHeight > 0
                && (element === document.scrollingElement || ['auto','scroll'].includes(getComputedStyle(element).overflowY))) result.push('scroll-up','scroll-down');
            return result;
          };
          let state = window[request.key];
          if (request.command === 'inspect') {
            if (!state) {
              state = {document, identity: request.documentId, snapshots: new Map()};
              Object.defineProperty(window, request.key, {value: state, configurable: false, enumerable: false, writable: false});
            }
            if (state.document !== document || state.identity !== request.documentId) return reply('rejected');
            let truncated = false, visited = 0, bytes = 0;
            const bound = value => {
              const runes = Array.from(String(value || '').slice(0, 512));
              if (runes.length > 128) truncated = true;
              return runes.slice(0, 128).join('');
            };
            const text = (element, descend) => {
              let value = '', count = 0;
              const pending = [element];
              while (pending.length && count++ < 64 && value.length < 256) {
                const node = pending.pop();
                if (node.nodeType === 3) { value += node.substringData(0, 256 - value.length); continue; }
                if (node !== element && (field(node) || privateNode(node))) continue;
                const children = node.childNodes;
                for (let i = Math.min(children.length, 64) - 1; i >= 0; i--) {
                  if (descend || children[i].nodeType === 3) pending.push(children[i]);
                }
                if (children.length > 64) truncated = true;
              }
              if (pending.length) truncated = true;
              return bound(value.trim());
            };
            const label = element => {
              if (protectedInput(element)) return 'Protected input';
              const explicit = element.getAttribute('aria-label');
              if (explicit) return bound(explicit);
              const labelled = (element.getAttribute('aria-labelledby') || '').slice(0, 1024).split(/\s+/).filter(Boolean).slice(0, 8);
              if (labelled.length) return bound(labelled.map(id => {
                const node = document.getElementById(id);
                return node && !field(node) && !privateNode(node) ? text(node, true) : '';
              }).join(' '));
              if (field(element)) {
                const labels = element.labels;
                let name = '';
                for (let i = 0; labels && i < Math.min(labels.length, 8); i++) name += text(labels[i], true) + ' ';
                return bound(name.trim());
              }
              return text(element, element instanceof HTMLButtonElement || element instanceof HTMLAnchorElement || element.hasAttribute('role'));
            };
            const elements = [], references = new Map(), encoder = new TextEncoder(), query = request.query || {};
            const original = query.snapshotId && state.snapshots.get(query.snapshotId);
            const selected = original && original.references.get(query.elementReference || query.within);
            if ((query.elementReference || query.within) && (!original || performance.now() - original.created >= 30000 || !selected || !visible(selected))) return reply('rejected');
            const matches = (value, filter) => filter == null || value.toLocaleLowerCase().includes(filter.toLocaleLowerCase());
            if (!document.body) return reply('inspected', [], true);
            const scope = selected || document.body;
            const walker = document.createTreeWalker(scope, NodeFilter.SHOW_ELEMENT);
            let element = scope;
            while (element) {
              if (++visited > 512 || elements.length >= 128) { truncated = true; break; }
              if (element.shadowRoot || ['IFRAME','CANVAS'].includes(element.tagName)) truncated = true;
              if (visible(element)) {
                const supported = actions(element), name = label(element);
                if (name || supported.length) {
                  const reference = crypto.randomUUID().replace(/-/g, '');
                  const role = bound(element.getAttribute('role') || ({BUTTON:'button',A:'link',INPUT:protectedInput(element) ? 'protected-input' : 'input',TEXTAREA:'textbox',SELECT:'combobox'}[element.tagName] || 'text')).slice(0,32);
                  if (query.role != null && role.toLocaleLowerCase() !== query.role.toLocaleLowerCase() || !matches(name, query.name) || !matches(name, query.text)) {
                    element = query.elementReference ? null : walker.nextNode(); continue;
                  }
                  const item = {reference, role, name, isEnabled: enabled(element), actions: supported};
                  const semantic = {};
                  if (!protectedInput(element)) {
                    if (element instanceof HTMLInputElement && element.type === 'checkbox') semantic.checked = String(element.checked);
                    else if (element.hasAttribute('aria-checked')) semantic.checked = bound(element.getAttribute('aria-checked'));
                    if (element.hasAttribute('aria-expanded')) semantic.expanded = bound(element.getAttribute('aria-expanded'));
                    if (element.hasAttribute('aria-selected')) semantic.selected = bound(element.getAttribute('aria-selected'));
                    if (query.includeValue && field(element) && !element.isContentEditable) semantic.value = bound(element.value);
                  }
                  if (Object.keys(semantic).length) item.state = semantic;
                  bytes += encoder.encode(JSON.stringify(item)).byteLength;
                  if (bytes > 32 * 1024) { truncated = true; break; }
                  references.set(reference, element); elements.push(item);
                }
              }
              element = query.elementReference ? null : walker.nextNode();
            }
            while (state.snapshots.size >= 8) state.snapshots.delete(state.snapshots.keys().next().value);
            state.snapshots.set(request.snapshotId, {created: performance.now(), references});
            return reply('inspected', elements, truncated);
          }
          if (!state || state.document !== document || state.identity !== request.documentId || !id(request.elementReference)) return reply('rejected');
          const snapshot = state.snapshots.get(request.snapshotId);
          const element = snapshot && snapshot.references.get(request.elementReference);
          if (request.command === 'bounds') {
            if (!snapshot || performance.now() - snapshot.created >= 30000 || !element || !visible(element)) return reply('rejected');
            const box = element.getBoundingClientRect(), x = Math.max(0, box.left), y = Math.max(0, box.top);
            return JSON.stringify({state:'bounds', bounds:{x,y,width:Math.max(0,Math.min(innerWidth,box.right)-x),height:Math.max(0,Math.min(innerHeight,box.bottom)-y),viewportWidth:innerWidth,viewportHeight:innerHeight}});
          }
          if (request.command !== 'input') return reply('rejected');
          if (!snapshot || performance.now() - snapshot.created >= 30000 || !element || !actions(element).includes(request.action)
              || (request.action === 'set-text' ? typeof request.text !== 'string' || request.text.length > 4096 : request.text !== null)) return reply('rejected');
          // The native evaluator is called once. The host records unknown before
          // evaluation and retains that identity even when this response is lost.
          switch (request.action) {
            case 'invoke': case 'toggle': element.click(); break;
            case 'set-text': {
              const prototype = element instanceof HTMLInputElement ? HTMLInputElement.prototype : HTMLTextAreaElement.prototype;
              Object.getOwnPropertyDescriptor(prototype, 'value').set.call(element, request.text);
              element.dispatchEvent(new Event('input', {bubbles:true}));
              element.dispatchEvent(new Event('change', {bubbles:true}));
              break;
            }
            case 'scroll-up': case 'scroll-down': element.scrollBy({top: Math.max(1, element.clientHeight * 0.8) * (request.action === 'scroll-up' ? -1 : 1), behavior:'instant'}); break;
            default: return reply('rejected');
          }
          return reply('dispatched');
        }
        """;
}
