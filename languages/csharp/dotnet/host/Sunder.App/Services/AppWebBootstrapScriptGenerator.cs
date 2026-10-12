using System.Text.Json;
using Sunder.Runtime.Client;

namespace Sunder.App.Services;

internal static class AppWebBootstrapScriptGenerator
{
    public static string Generate(
        string protocolName,
        int protocolVersion,
        string expectedOrigin,
        string sessionNonce,
        RuntimeClientResourceLimits resourceProfile)
    {
        var protocol = JsonSerializer.Serialize(protocolName);
        var nonce = JsonSerializer.Serialize(sessionNonce);
        var origin = JsonSerializer.Serialize(expectedOrigin);
        var profileHash = JsonSerializer.Serialize(resourceProfile.Profile?.EffectiveProfileHash);
        var runtimeInstanceId = JsonSerializer.Serialize(resourceProfile.Profile?.RuntimeInstanceId.ToString("D"));
        var reportedByRuntime = resourceProfile.IsRuntimeReported ? "true" : "false";
        var policy = resourceProfile.EffectivePolicy;
        return FormattableString.Invariant($$$"""
            (() => {
              'use strict';
              const protocol = {{{protocol}}};
              const version = {{{protocolVersion}}};
              const nonce = {{{nonce}}};
              const expectedOrigin = {{{origin}}};
              const encoder = new TextEncoder();
              const maxEnvelopeBytes = {{{resourceProfile.MaximumJsonEnvelopeBytes}}};
              const maxPendingRequests = {{{RuntimeClientResourceLimits.MaximumPendingBrowserRequests}}};
              const maxRequestMilliseconds = {{{(long)RuntimeClientResourceLimits.BrowserRequestTimeout.TotalMilliseconds}}};
              const maxQueuedEvents = {{{RuntimeClientResourceLimits.MaximumQueuedBrowserEvents}}};
              const maxQueuedEventBytes = {{{resourceProfile.MaximumQueuedBrowserEventBytes}}};
              const resourceProfile = Object.freeze({
                reportedByRuntime: {{{reportedByRuntime}}},
                runtimeInstanceId: {{{runtimeInstanceId}}},
                effectiveProfileHash: {{{profileHash}}},
                effectivePolicy: Object.freeze({
                  rpcValueBytes: {{{policy.MaximumRpcValueBytes}}},
                  valueDepth: {{{policy.MaximumValueDepth}}},
                  valueNodes: {{{policy.MaximumValueNodes}}},
                  objectMembers: {{{policy.MaximumObjectMembers}}},
                  inlineDecodedBytes: {{{policy.MaximumInlineDecodedBytes}}},
                  contentReferenceBytes: {{{policy.MaximumContentReferenceBytes}}}
                })
              });
              if (location.origin !== expectedOrigin || typeof window.invokeCSharpAction !== 'function') return false;
              const marker = `__sunderBridge_${nonce}`;
              if (window[marker] === true && window.sunder?.protocol === protocol && window.sunder?.version === version && typeof window.__sunderReceive === 'function') return window.sunder.signal.aborted === false;
              if (Object.prototype.hasOwnProperty.call(window, 'sunder')) return false;
              const lifetime = new AbortController();
              let retired = false;
              let sequence = 0;
              const pending = new Map();
              let queuedEventBytes = 0;
              const freezeJson = (value) => {
                if (value !== null && typeof value === 'object' && !Object.isFrozen(value)) {
                  for (const child of Object.values(value)) freezeJson(child);
                  Object.freeze(value);
                }
                return value;
              };
              const failure = (kind, code, message, dispatchState = 'not-dispatched', retrySafety = 'safe', details) => {
                const error = { kind, code, message, dispatchState, retrySafety };
                if (details !== undefined) error.details = freezeJson(details);
                return Object.assign(new Error(message), {
                  name: 'SunderRpcError',
                  error: Object.freeze(error)
                });
              };
              const depth = (root) => {
                let maximum = 1;
                const stack = [{ value: root, depth: 1 }];
                while (stack.length) {
                  const current = stack.pop();
                  maximum = Math.max(maximum, current.depth);
                  if (current.value !== null && typeof current.value === 'object') {
                    for (const value of Object.values(current.value)) stack.push({ value, depth: current.depth + 1 });
                  }
                }
                return maximum;
              };
              const rejected = (error, stream) => {
                if (!stream) return Promise.reject(error);
                return Object.freeze({
                  [Symbol.asyncIterator]() {
                    let returned = false;
                    return {
                      next() {
                        if (returned) return Promise.resolve({ done: true, value: undefined });
                        returned = true;
                        return Promise.reject(error);
                      },
                      return() { returned = true; return Promise.resolve({ done: true, value: undefined }); }
                    };
                  }
                });
              };
              const finish = (state, error) => {
                if (state.done) return;
                state.done = true;
                state.error = error;
                if (state.timer !== null) clearTimeout(state.timer);
                state.signal?.removeEventListener('abort', state.cancel);
                pending.delete(state.requestId);
                for (const item of state.queue.splice(0)) queuedEventBytes -= item.size;
                for (const waiter of state.waiters.splice(0)) error ? waiter.fail(error) : waiter.ok({ done: true, value: undefined });
                if (!state.stream && error) state.reject(error);
              };
              const send = (method, payload, options, stream) => {
                if (retired) return rejected(failure('unavailable', 'rpc.bridge.retired', 'The browser bridge document has retired.'), stream);
                const signal = options?.signal;
                if (signal?.aborted) return rejected(failure('cancelled', 'rpc.bridge.cancelled', 'The browser bridge request was cancelled.'), stream);
                const requestId = `b_${++sequence}`;
                let body;
                let normalized;
                try {
                  body = JSON.stringify({ protocol, version, nonce, origin: location.origin, requestId, method, deadlineUtc: options?.deadline?.toISOString?.() ?? null, payload });
                  if (typeof body !== 'string') throw new TypeError('Request serialization returned no JSON.');
                  normalized = JSON.parse(body);
                } catch {
                  return rejected(failure('validation', 'rpc.bridge.serialization', 'The browser bridge request is not valid JSON.'), stream);
                }
                const value = method === 'rpc.invoke' || method === 'rpc.subscribe'
                  ? normalized.payload?.request
                  : normalized.payload;
                let valueJson;
                try { valueJson = JSON.stringify(value); } catch { valueJson = undefined; }
                if (typeof valueJson !== 'string'
                    || encoder.encode(valueJson).byteLength > resourceProfile.effectivePolicy.rpcValueBytes
                    || depth(value) > resourceProfile.effectivePolicy.valueDepth
                    || encoder.encode(body).byteLength > maxEnvelopeBytes) {
                  return rejected(failure('resource-exhausted', 'rpc.bridge.request-limit', 'The browser bridge request exceeds a negotiated resource limit.'), stream);
                }
                if (pending.size >= maxPendingRequests) {
                  return rejected(failure('resource-exhausted', 'rpc.bridge.pending-limit', 'The browser bridge request limit was reached.'), stream);
                }
                // Serialization can execute package-owned toJSON/getters. Recheck before retaining work.
                if (retired) return rejected(failure('unavailable', 'rpc.bridge.retired', 'The browser bridge document has retired.'), stream);
                if (signal?.aborted) return rejected(failure('cancelled', 'rpc.bridge.cancelled', 'The browser bridge request was cancelled.'), stream);
                let resolve = () => {};
                let reject = () => {};
                const promise = stream ? null : new Promise((ok, fail) => { resolve = ok; reject = fail; });
                const state = { requestId, resolve, reject, stream, queue: [], waiters: [], done: false, error: null, signal, dispatched: false, cancelSent: false, timer: null };
                const notifyCancel = () => {
                  if (state.done || state.cancelSent || !state.dispatched) return;
                  state.cancelSent = true;
                  try {
                    window.invokeCSharpAction(JSON.stringify({ protocol, version, nonce, origin: location.origin, requestId, method: 'bridge.cancel', deadlineUtc: null, payload: { targetRequestId: requestId } }));
                  } catch {
                    finish(state, failure('unavailable', 'rpc.bridge.native-unavailable', 'The native browser bridge is unavailable.', 'possibly-dispatched', 'unsafe'));
                  }
                };
                const cancel = () => {
                  if (state.done) return;
                  notifyCancel();
                  finish(state, failure('cancelled', 'rpc.bridge.cancelled', 'The browser bridge request was cancelled.', state.dispatched ? 'possibly-dispatched' : 'not-dispatched', state.dispatched ? 'unsafe' : 'safe'));
                };
                state.cancel = cancel;
                state.notifyCancel = notifyCancel;
                pending.set(requestId, state);
                const requestedDeadline = Date.parse(normalized.deadlineUtc ?? '');
                const remaining = Number.isFinite(requestedDeadline)
                  ? Math.max(0, requestedDeadline - Date.now())
                  : maxRequestMilliseconds;
                const timeout = Math.min(maxRequestMilliseconds, remaining);
                state.timer = setTimeout(() => {
                  if (state.done) return;
                  notifyCancel();
                  finish(state, failure('deadline-exceeded', 'rpc.call.deadline-exceeded', 'The browser bridge request deadline elapsed.', 'possibly-dispatched', 'unsafe'));
                }, timeout);
                signal?.addEventListener('abort', cancel, { once: true });
                if (signal?.aborted) cancel();
                if (!state.done) {
                  try {
                    state.dispatched = true;
                    window.invokeCSharpAction(body);
                  } catch {
                    finish(state, failure('unavailable', 'rpc.bridge.native-unavailable', 'The native browser bridge is unavailable.', 'possibly-dispatched', 'unsafe'));
                  }
                }
                if (!stream) return promise;
                return Object.freeze({
                  [Symbol.asyncIterator]() {
                    return {
                      next() {
                        if (state.queue.length) {
                          const item = state.queue.shift();
                          queuedEventBytes -= item.size;
                          return Promise.resolve({ done: false, value: item.value });
                        }
                        if (state.done) return state.error ? Promise.reject(state.error) : Promise.resolve({ done: true, value: undefined });
                        return new Promise((ok, fail) => state.waiters.push({ ok, fail }));
                      },
                      return() { cancel(); return Promise.resolve({ done: true, value: undefined }); }
                    };
                  }
                });
              };
              const retire = (notifyNative = false) => {
                if (retired) return;
                retired = true;
                window.removeEventListener('pagehide', pageHidden);
                for (const state of [...pending.values()]) {
                  if (notifyNative) state.notifyCancel();
                  finish(state, failure('unavailable', 'rpc.bridge.retired', 'The browser bridge document has retired.', state.dispatched ? 'possibly-dispatched' : 'not-dispatched', state.dispatched ? 'unsafe' : 'safe'));
                }
                lifetime.abort(failure('unavailable', 'rpc.bridge.retired', 'The browser bridge document has retired.'));
              };
              const pageHidden = () => retire(true);
              Object.defineProperty(window, '__sunderReceive', { configurable: true, value(raw) {
                let message;
                try { message = JSON.parse(raw); } catch { return; }
                if (message?.protocol !== protocol || message?.version !== version || message?.nonce !== nonce) return;
                if (retired) return;
                if (message.type === 'retired' && message.requestId === null && message.value === null && message.error === null) {
                  retire();
                  return;
                }
                const state = pending.get(message.requestId);
                if (!state || state.done) return;
                const emittedBytes = encoder.encode(raw).byteLength;
                if (emittedBytes > maxEnvelopeBytes) {
                  state.notifyCancel();
                  finish(state, failure('resource-exhausted', 'rpc.bridge.response-envelope-limit', 'The browser bridge response exceeds its negotiated envelope limit.', 'possibly-dispatched', 'unsafe'));
                  return;
                }
                if (message.type === 'event' || message.type === 'result') {
                  let valueJson;
                  try { valueJson = JSON.stringify(message.value); } catch { valueJson = undefined; }
                  if (typeof valueJson !== 'string'
                      || encoder.encode(valueJson).byteLength > resourceProfile.effectivePolicy.rpcValueBytes
                      || depth(message.value) > resourceProfile.effectivePolicy.valueDepth) {
                    state.notifyCancel();
                    finish(state, failure('resource-exhausted', 'rpc.bridge.response-limit', 'The browser bridge response exceeds a negotiated resource limit.', 'possibly-dispatched', 'unsafe'));
                    return;
                  }
                }
                if (message.type === 'event') {
                  if (!state.stream) {
                    finish(state, failure('protocol', 'rpc.bridge.unexpected-event', 'The browser bridge returned an unexpected stream event.', 'possibly-dispatched', 'unsafe'));
                    return;
                  }
                  const waiter = state.waiters.shift();
                  if (waiter) {
                    waiter.ok({ done: false, value: message.value });
                  } else if (state.queue.length >= maxQueuedEvents || queuedEventBytes + emittedBytes > maxQueuedEventBytes) {
                    state.notifyCancel();
                    finish(state, failure('resource-exhausted', 'rpc.bridge.event-queue-limit', 'The browser bridge event queue limit was reached.', 'possibly-dispatched', 'unsafe'));
                  } else {
                    state.queue.push({ value: message.value, size: emittedBytes });
                    queuedEventBytes += emittedBytes;
                  }
                  return;
                }
                if (message.type === 'result') {
                  if (state.stream) finish(state, failure('protocol', 'rpc.bridge.unexpected-result', 'The browser bridge returned an unexpected unary result.', 'possibly-dispatched', 'unsafe'));
                  else { state.resolve(message.value); finish(state); }
                  return;
                }
                if (message.type === 'complete') {
                  if (state.stream) finish(state);
                  else finish(state, failure('protocol', 'rpc.bridge.unexpected-complete', 'The browser bridge completed without a unary result.', 'possibly-dispatched', 'unsafe'));
                  return;
                }
                if (message.type === 'error') {
                  const detail = message.error ?? {};
                  const dispatchState = detail.dispatchState === 'not-dispatched' ? 'not-dispatched' : 'possibly-dispatched';
                  const retrySafety = detail.retrySafety === 'safe' ? 'safe' : 'unsafe';
                  let details;
                  if (Object.prototype.hasOwnProperty.call(detail, 'details')) {
                    let detailsJson;
                    try { detailsJson = JSON.stringify(detail.details); } catch { detailsJson = undefined; }
                    if (detail.kind !== 'domain'
                        || typeof detailsJson !== 'string'
                        || encoder.encode(detailsJson).byteLength > resourceProfile.effectivePolicy.rpcValueBytes
                        || depth(detail.details) > resourceProfile.effectivePolicy.valueDepth) {
                      finish(state, failure('protocol', 'rpc.bridge.invalid-error-details', 'The browser bridge returned invalid RPC error details.', dispatchState, retrySafety));
                      return;
                    }
                    details = detail.details;
                  }
                  finish(state, failure(detail.kind ?? 'protocol', detail.code ?? 'rpc.bridge.error', detail.message ?? 'Sunder RPC failed.', dispatchState, retrySafety, details));
                  return;
                }
                finish(state, failure('protocol', 'rpc.bridge.message-type', 'The browser bridge returned an unknown message type.', 'possibly-dispatched', 'unsafe'));
              }});
              const rpc = Object.freeze({
                discover: (contractId, options) => send('rpc.discover', { contractId }, options, false),
                watch: (afterRevision, afterSequence, options) => send('rpc.watch', { afterRevision, afterSequence }, options, true),
                invoke: (providerHandle, serviceId, methodId, request, options) => send('rpc.invoke', { providerHandle, serviceId, methodId, request }, options, false),
                subscribe: (providerHandle, serviceId, methodId, request, options) => send('rpc.subscribe', { providerHandle, serviceId, methodId, request }, options, true)
              });
              const api = Object.freeze({
                protocol,
                version,
                signal: lifetime.signal,
                resourceProfile,
                rpc,
                openExternal: (url, options) => send('host.openExternal', { url }, options, false),
                getNavigationState: (options) => send('navigation.getState', {}, options, false),
                navigate: (route, options) => send('navigation.navigate', { route }, options, false)
              });
              Object.defineProperty(window, 'sunder', { configurable: false, enumerable: true, writable: false, value: api });
              Object.defineProperty(window, marker, { configurable: false, enumerable: false, writable: false, value: true });
              window.addEventListener('pagehide', pageHidden, { once: true });
              window.dispatchEvent(new CustomEvent('sunder:ready', { detail: api }));
              return true;
            })();
            """);
    }

    public static string GenerateRetirement(string protocolName, int protocolVersion, string sessionNonce)
    {
        var envelope = JsonSerializer.Serialize(new
        {
            protocol = protocolName,
            version = protocolVersion,
            nonce = sessionNonce,
            requestId = (string?)null,
            type = "retired",
            value = (object?)null,
            error = (object?)null,
        });
        return $"window.__sunderReceive?.({JsonSerializer.Serialize(envelope)});";
    }
}
