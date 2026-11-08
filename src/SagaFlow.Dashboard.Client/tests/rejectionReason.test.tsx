import { render, screen } from "@testing-library/react";
import { expect, it } from "vitest";
import { InvocationDetails } from "../src/components/InstanceTraceability";
import { RuntimeRelationshipDetails } from "../src/components/BigPicture/RuntimeRelationshipDetails";
import type { InvocationView, InstanceRelationship } from "../src/api/sagaflowApi";
const process = { domain: "orders", process: "order-process", version: "1.0" };
it.each(["Domain admission declined the request.", null])("Source traceability renders only provided technical reason: %s", reason => {
  const invocation: InvocationView & { result: unknown } = { invocationId: "inv-1", target: process,
    targetSagaInstanceId: null, lastKnownStatus: "Rejected", rejectionReason: reason, createdAt: null,
    updatedAt: null, lastObservedAt: null, reconciliation: null, result: { rejectionReason: "business-result-decoy" } };
  render(<InvocationDetails invocation={invocation} localStatus="Failed" />);
  expect(screen.getByText("Rejected")).toBeTruthy(); expect(screen.getByText("inv-1")).toBeTruthy();
  expect(screen.queryByText("business-result-decoy")).toBeNull();
  if (reason) { expect(screen.getByText("Rejection reason")).toBeTruthy(); expect(screen.getByText(reason)).toBeTruthy(); }
  else expect(screen.queryByText("Rejection reason")).toBeNull();
});
it.each(["Order acceptance validation failed.", null])("BigPicture relationship renders only provided technical reason: %s", reason => {
  const row: InstanceRelationship & { result: unknown } = { invocationId: "inv-2", perspective: "Source", source: process,
    sourceSagaInstanceId: "source-id", sourceStep: "invoke", sourceCorrelationId: "source-correlation", target: process,
    targetSagaInstanceId: null, targetCorrelationId: null, lastKnownStatus: "Rejected", targetLocalStatus: null,
    createdAt: null, updatedAt: null, lastObservedAt: null, reconciliation: { reason: "Manual check", requiredAt: "2026-11-05T00:00:00Z" },
    rejectionReason: reason, result: { rejectionReason: "business-result-decoy" } };
  render(<RuntimeRelationshipDetails row={row} />);
  expect(screen.getByText("Rejected")).toBeTruthy(); expect(screen.getByText("source-correlation")).toBeTruthy();
  expect(screen.getByText("Manual check")).toBeTruthy(); expect(screen.queryByText("business-result-decoy")).toBeNull();
  if (reason) { expect(screen.getByText("Rejection reason")).toBeTruthy(); expect(screen.getByText(reason)).toBeTruthy(); }
  else expect(screen.queryByText("Rejection reason")).toBeNull();
});
