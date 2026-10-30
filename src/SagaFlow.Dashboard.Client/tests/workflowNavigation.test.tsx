import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import definitions from "./fixtures/order-definition.json";
import { getDefinitions, getInstances, type SagaDefinition } from "../src/api/sagaflowApi";
import { DashboardLayout } from "../src/layout/DashboardLayout";
import { ThemeProvider } from "../src/theme/ThemeProvider";
import { WorkflowNodeDetails } from "../src/components/Workflow/WorkflowNodeDetails";

vi.mock("../src/api/sagaflowApi", () => ({ getDefinitions: vi.fn(), getInstances: vi.fn(), getInstance: vi.fn() }));
vi.mock("../src/live/useSagaFlowLiveUpdates", () => ({ useSagaFlowLiveUpdates: vi.fn() }));
// Navigation tests exercise real page state; graph geometry is covered separately with the real ELK implementation.
vi.mock("../src/components/Workflow/WorkflowGraph", () => ({ default: ({ definition }: { definition: SagaDefinition }) => <div role="img" aria-label={`${definition.name} workflow diagram`} /> }));

beforeEach(() => {
  vi.resetAllMocks();
  vi.mocked(getDefinitions).mockResolvedValue(definitions);
  vi.mocked(getInstances).mockResolvedValue([]);
  vi.stubGlobal("matchMedia", vi.fn(() => ({ matches: false })));
});

afterEach(() => vi.unstubAllGlobals());

function openDashboard() {
  return render(<ThemeProvider><DashboardLayout /></ThemeProvider>);
}

describe("workflow navigation", () => {
  it("starts with one local workflow list, opens only the selected workflow, and returns via its breadcrumb", async () => {
    const user = userEvent.setup();
    const second: SagaDefinition = { ...definitions[0], name: "return-flow", domain: "returns", version: "2.0" };
    vi.mocked(getDefinitions).mockResolvedValue([...definitions, second]);
    openDashboard();
    await user.click(await screen.findByRole("button", { name: /order-process/ }));
    expect(await screen.findByRole("img", { name: "order-process workflow diagram" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Expand navigation" }).getAttribute("aria-expanded")).toBe("false");
    await user.click(screen.getByRole("button", { name: "Expand navigation" }));
    expect(screen.getByRole("button", { name: "Collapse navigation" }).getAttribute("aria-expanded")).toBe("true");
    expect(screen.getByRole("img", { name: "order-process workflow diagram" })).toBeTruthy();
    expect(screen.queryByRole("img", { name: "return-flow workflow diagram" })).toBeNull();
    const breadcrumb = screen.getByRole("navigation", { name: "Workflow breadcrumb" });
    expect(within(breadcrumb).getByText("order-process")).toBeTruthy();
    await user.click(within(breadcrumb).getByRole("button", { name: "Workflows" }));
    expect(await screen.findByRole("button", { name: /return-flow/ })).toBeTruthy();
    expect(screen.queryByRole("img")).toBeNull();
  });

  it("closes mobile navigation on workflow selection without introducing a collapsed rail", async () => {
    vi.mocked(window.matchMedia).mockReturnValue({ matches: true } as MediaQueryList);
    const user = userEvent.setup();
    openDashboard();
    await user.click(screen.getByRole("button", { name: "Open navigation" }));
    expect(screen.getByRole("button", { name: "Close navigation backdrop" })).toBeTruthy();
    await user.click(await screen.findByRole("button", { name: /order-process/ }));
    expect(await screen.findByRole("img", { name: "order-process workflow diagram" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Close navigation backdrop" })).toBeNull();
    expect(screen.getByRole("button", { name: "Open navigation" }).getAttribute("aria-expanded")).toBe("false");
    expect(screen.queryByRole("button", { name: "Expand navigation" })).toBeNull();
  });

  it("keeps the two-item sidebar and existing Instances list accessible", async () => {
    const user = userEvent.setup();
    const { container } = openDashboard();
    await screen.findByRole("button", { name: /order-process/ });
    const sidebar = within(container.querySelector("aside")!);
    expect(sidebar.getByRole("button", { name: "Workflows", exact: true })).toBeTruthy();
    expect(sidebar.getByRole("button", { name: "Instances", exact: true })).toBeTruthy();
    expect(sidebar.queryByRole("button", { name: "Overview", exact: true })).toBeNull();
    await user.click(sidebar.getByRole("button", { name: "Instances", exact: true }));
    expect(await screen.findByText("No saga instances.")).toBeTruthy();
    expect(getInstances).toHaveBeenCalled();
    await user.click(sidebar.getByRole("button", { name: "Workflows", exact: true }));
    expect(await screen.findByRole("button", { name: /order-process/ })).toBeTruthy();
  });

  it("shows load failures and supports retry without displaying a fake workflow", async () => {
    vi.mocked(getDefinitions).mockRejectedValueOnce(new Error("Definitions unavailable"));
    const user = userEvent.setup();
    openDashboard();
    expect((await screen.findByRole("alert")).textContent).toBe("Definitions unavailable");
    expect(screen.queryByRole("button", { name: /order-process/ })).toBeNull();
    await user.click(screen.getByRole("button", { name: "Refresh workflows" }));
    expect(await screen.findByRole("button", { name: /order-process/ })).toBeTruthy();
  });

  it("retains declared workflow references as metadata without fake navigation links", async () => {
    vi.mocked(getDefinitions).mockResolvedValue([{ ...definitions[0], bigPicture: [{ domain: "accounts", process: "reconciliation", version: "2.0", endpoint: "https://accounts.example" }] }]);
    const user = userEvent.setup();
    openDashboard();
    await user.click(await screen.findByRole("button", { name: /order-process/ }));
    await screen.findByRole("img", { name: "order-process workflow diagram" });
    expect(screen.getByText("accounts / reconciliation")).toBeTruthy();
    expect(screen.queryByRole("link", { name: /reconciliation/ })).toBeNull();
  });

  it("shows actual node metadata and optional fields without inventing a timeout", () => {
    const step = definitions[0].steps.find(step => step.name === "managerApproval")!;
    render(<WorkflowNodeDetails node={{ kind: "step", name: step.name, step, incoming: [], outgoing: [] }} />);
    expect(screen.getByText("RequestManagerApproval")).toBeTruthy();
    expect(screen.getByText("steps.managerApproval.result.approved = true")).toBeTruthy();
    expect(screen.getAllByText("Default (unconditional fallback)")).toHaveLength(2);
    expect(screen.queryByText("Timeout", { exact: true })).toBeNull();
    expect(screen.queryByText("Compensation command")).toBeNull();
    expect(screen.getByText("Max attempts (metadata)")).toBeTruthy();
  });
});
