import { RotateCcw } from "lucide-react";

import { EmptyPanel, PageSection, SummaryPanel } from "../layout/DashboardLayout";

export function CompensationPage() {
  return (
    <PageSection>
      <SummaryPanel
        icon={<RotateCcw className="size-4" />}
        title="Consistency Management"
        description="Failure detection, rollback, and compensation activity will appear here."
        metric="0 pending"
      />
      <EmptyPanel
        icon={<RotateCcw size={20} strokeWidth={2} aria-hidden="true" />}
        title="No compensation activity."
        description="Rollback and compensation commands will be visible here during failed Saga flows."
      />
    </PageSection>
  );
}
