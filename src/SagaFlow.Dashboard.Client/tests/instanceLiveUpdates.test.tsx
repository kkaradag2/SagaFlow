import { act, render } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { getHubPath, useSagaFlowLiveUpdates } from "../src/live/useSagaFlowLiveUpdates";
const hub = vi.hoisted(() => ({ on: vi.fn(), off: vi.fn(), start: vi.fn().mockResolvedValue(undefined), stop: vi.fn().mockResolvedValue(undefined), onreconnected: vi.fn() }));
vi.mock("@microsoft/signalr", () => ({
  HubConnectionState: { Disconnected: undefined }, LogLevel: { Warning: 3 },
  HubConnectionBuilder: class {
    withUrl() { return this; } withAutomaticReconnect() { return this; } configureLogging() { return this; } build() { return hub; }
  },
}));
beforeEach(() => { vi.clearAllMocks(); });
afterEach(() => document.querySelector("base")?.remove());
function Watch({ changed, enabled = true }: { changed: (id: string | null) => void; enabled?: boolean }) { useSagaFlowLiveUpdates(changed, enabled); return null; }
it("does not subscribe in definition-only mode and cleans up when context is cleared", () => {
  const view = render(<Watch changed={vi.fn()} enabled={false} />);
  expect(hub.on).not.toHaveBeenCalled();
  view.rerender(<Watch changed={vi.fn()} enabled={true} />); expect(hub.on).toHaveBeenCalledTimes(1);
  view.rerender(<Watch changed={vi.fn()} enabled={false} />); expect(hub.off).toHaveBeenCalledTimes(1); expect(hub.stop).toHaveBeenCalledTimes(1);
});
it("resolves the configured Dashboard base on a deep instance URL", () => {
  window.history.replaceState(null, "", "/custom/saga/instances");
  const base = document.createElement("base"); base.href = "/custom/saga/"; document.head.append(base);
  expect(getHubPath()).toBe("/custom/saga/hub");
});
it("refreshes only from valid events, refreshes on reconnect, and keeps the latest selection callback", async () => {
  const first = vi.fn(); const latest = vi.fn();
  const view = render(<Watch changed={first} />);
  const callback = hub.on.mock.calls[0][1] as (id: string) => void;
  await act(async () => callback("source-instance"));
  expect(first).toHaveBeenCalledWith("source-instance");
  view.rerender(<Watch changed={latest} />);
  await act(async () => callback("target-instance"));
  expect(latest).toHaveBeenCalledWith("target-instance");
  await act(async () => callback("")); expect(latest).toHaveBeenCalledTimes(1);
  await act(async () => (hub.onreconnected.mock.calls[0][0] as () => void)());
  expect(latest).toHaveBeenLastCalledWith(null);
  view.unmount(); expect(hub.off).toHaveBeenCalled(); expect(hub.stop).toHaveBeenCalled();
});
