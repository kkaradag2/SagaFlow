import { act, fireEvent, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, afterEach, expect, it, vi } from "vitest";
import { InstancesPage } from "../src/pages/InstancesPage";
import { getInstances, getInstance, type SagaInstanceDetail } from "../src/api/sagaflowApi";
import { IncomingOrigin } from "../src/components/InstanceTraceability";
vi.mock("../src/api/sagaflowApi", () => ({ getInstances: vi.fn(), getInstance: vi.fn() }));
let live: (id: string | null) => void | Promise<void>;
vi.mock("../src/live/useSagaFlowLiveUpdates", () => ({ useSagaFlowLiveUpdates: (callback: typeof live) => { live = callback; } }));
const time = "2026-11-04T12:00:00Z";
function detail(): SagaInstanceDetail {
  return { id: "source-saga", processName: "source-flow", definitionDomain: "source", definitionVersion: "1.0", origin: "Local",
    correlationId: "source-correlation", status: "Running", currentStep: "remote", createdAt: time, updatedAt: time,
    failureReason: null, incomingInvocation: null, compensations: [], domainRollback: null,
    domainStarter: { operationName: "Start", rollbackOperationName: "Rollback", status: null, startedAt: null, completedAt: null, input: null, result: null },
    steps: [{ stepName: "remote", command: "InvokeRemote", external: true, status: "Dispatched", executionOrder: 1,
      startedAt: time, completedAt: null, timedOutAt: null, timeoutLimit: null, input: null, result: null,
      invocation: { invocationId: "invocation-1", target: { domain: "target", process: "remote-flow", version: "2.0" },
        targetSagaInstanceId: null, lastKnownStatus: "Prepared", createdAt: time, updatedAt: time, lastObservedAt: null, reconciliation: null } }] };
}
beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(getInstances).mockResolvedValue([detail()]); vi.mocked(getInstance).mockResolvedValue(detail());
  vi.stubGlobal("requestAnimationFrame", (callback: FrameRequestCallback) => { callback(0); return 1; });
});
afterEach(() => { vi.unstubAllGlobals(); document.documentElement.classList.remove("dark"); });
async function open() {
  const user = userEvent.setup(); render(<InstancesPage />);
  await user.click(await screen.findByRole("button", { name: /source-flow/ })); return user;
}
it("distinguishes local step from last known external status and never invents remote links", async () => {
  await open();
  expect(screen.getAllByText("External process")).toHaveLength(2);
  expect(screen.getByText("Waiting for an external process outcome. No step timeout is configured.")).toBeTruthy();
  const panel = screen.getByRole("region", { name: "External invocation relationship" });
  expect(within(panel).getByText("Prepared")).toBeTruthy();
  expect(within(panel).getByText("Dispatched")).toBeTruthy();
  expect(within(panel).getByText("Not known yet")).toBeTruthy();
  expect(within(panel).getByText("target/remote-flow@2.0")).toBeTruthy();
  expect(within(panel).queryByRole("link")).toBeNull();
  expect(screen.getByText(/Accepted does not mean completed/)).toBeTruthy();
});
it.each(["Prepared", "Requested", "Accepted", "Running", "Completed", "Failed", "Compensated", "Rejected"])("refreshes %s observations using the existing live callback", async status => {
  await open();
  const next = detail(); next.steps[0].invocation = { ...next.steps[0].invocation!, lastKnownStatus: status, targetSagaInstanceId: "target-saga", lastObservedAt: time };
  vi.mocked(getInstance).mockResolvedValue(next);
  await act(async () => { await live("source-saga"); });
  expect(within(screen.getByRole("region", { name: "External invocation relationship" })).getByText(status)).toBeTruthy();
  expect(screen.getByText("target-saga")).toBeTruthy();
  expect(screen.getByText(["Completed", "Failed", "Compensated", "Rejected"].includes(status) ? "External result recorded; local continuation is pending. No step timeout is configured." : "Waiting for an external process outcome. No step timeout is configured.")).toBeTruthy();
  await act(async () => { await live("source-saga"); });
  expect(screen.getAllByText("target-saga")).toHaveLength(1);
});
it("retains failed source and reconciliation warning after remote Completed, even with details collapsed", async () => {
  await open(); const next = detail(); next.status = "Failed"; next.currentStep = null; next.steps[0].status = "Failed";
  next.steps[0].invocation = { ...next.steps[0].invocation!, lastKnownStatus: "Completed", reconciliation: { reason: "Local publish failed after remote request", requiredAt: time } };
  vi.mocked(getInstance).mockResolvedValue(next); await act(async () => { await live(null); });
  const alert = screen.getByRole("alert"); expect(within(alert).getByText("Manual reconciliation required")).toBeTruthy();
  expect(within(alert).getByText(/Local step remote: Failed/)).toBeTruthy();
  await userEvent.click(screen.getByRole("button", { name: "Hide external process details: InvokeRemote" }));
  expect(screen.getByRole("alert")).toBeTruthy();
  expect(screen.queryByRole("region", { name: "External invocation relationship" })).toBeNull();
  expect(screen.queryByText("Waiting for an external process outcome.")).toBeNull();
});
it("shows incoming source identity separately from target-local identity and copies without navigation", async () => {
  const target = detail(); target.id = "target-saga"; target.correlationId = "target-local-correlation"; target.origin = "External";
  target.incomingInvocation = { invocationId: "invocation-1", source: { domain: "source", process: "source-flow", version: "1.0" },
    sourceSagaInstanceId: "source-saga", sourceStep: "remote", sourceCorrelationId: "source-correlation", sourceRequestCreatedAt: time, acceptedAt: time };
  const user = userEvent.setup(); const write = vi.fn().mockResolvedValue(undefined);
  Object.defineProperty(navigator, "clipboard", { value: { writeText: write }, configurable: true });
  render(<IncomingOrigin instance={target} />);
  expect(screen.getByText("source/source-flow@1.0")).toBeTruthy();
  expect(screen.getByText("source-correlation")).toBeTruthy(); expect(screen.getByText("target-local-correlation")).toBeTruthy();
  await user.click(screen.getByRole("button", { name: "Copy source Saga instance ID" }));
  expect(write).toHaveBeenCalledWith("source-saga"); expect(screen.getByRole("status").textContent).toBe("Copied");
  expect(screen.queryByRole("link")).toBeNull();
});
it("keeps legacy local instances and expansion usable in dark mode", async () => {
  const local = detail(); local.steps[0].external = false; local.steps[0].invocation = null; delete local.origin;
  vi.mocked(getInstance).mockResolvedValue(local); document.documentElement.classList.add("dark");
  await open(); expect(screen.queryByText("External process")).toBeNull(); expect(screen.getByText("Origin: Local")).toBeTruthy();
  const button = screen.getByRole("button", { name: "Hide forward details: InvokeRemote" }); fireEvent.click(button);
  expect(screen.getByRole("button", { name: "Show forward details: InvokeRemote" })).toBeTruthy();
  expect(document.documentElement.classList.contains("dark")).toBe(true);
});
