import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import definitions from "./fixtures/order-definition.json";
import { WorkflowNodeCard } from "../src/components/Workflow/WorkflowNodeCard";
import { createWorkflowGraph, type WorkflowNode } from "../src/components/Workflow/workflowMapping";

vi.mock("@xyflow/react", async importOriginal => ({ ...await importOriginal<typeof import("@xyflow/react")>(), Handle: () => null }));

function showNode(node: WorkflowNode) {
  return render(<WorkflowNodeCard id={node.id} type="workflow" data={node.data} width={node.width} height={node.height}
    selected={false} dragging={false} draggable={false} selectable deletable={false} isConnectable={false}
    zIndex={0} positionAbsoluteX={0} positionAbsoluteY={0} />);
}

describe("workflow node presentation", () => {
  it("shows a concise condition expression and retains the full expression in its tooltip", () => {
    const node = createWorkflowGraph(definitions[0]).nodes.find(node => node.id === "condition:managerApproval:success:0")!;
    const { container } = showNode(node);
    expect(screen.getByText("approved = true")).toBeTruthy();
    expect(screen.queryByText("steps.managerApproval.result.approved")).toBeNull();
    expect(container.querySelector('[data-workflow-kind="condition"]')?.getAttribute("title")).toBe("steps.managerApproval.result.approved = true");
  });

  it("keeps the command and step name without sequence badges", () => {
    const graph = createWorkflowGraph(definitions[0]);
    const { unmount } = showNode(graph.nodes[0]);
    expect(screen.getByText("AuthorizePayment")).toBeTruthy();
    expect(screen.getByText("payment")).toBeTruthy();
    expect(screen.queryByText(/^start$/i)).toBeNull();
    unmount();
    showNode(graph.nodes[1]);
    expect(screen.getByText("ReserveInventory")).toBeTruthy();
    expect(screen.queryByText(/^step\s+\d+$/i)).toBeNull();
  });
});
