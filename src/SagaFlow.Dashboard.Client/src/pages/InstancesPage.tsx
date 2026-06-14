import { Boxes } from "lucide-react";

import { EmptyPanel, PageSection, SummaryPanel } from "../layout/DashboardLayout";

export function InstancesPage() {
  return (
    <PageSection>
      <SummaryPanel
        icon={<Boxes className="size-4" />}
        title="Saga Instances"
        description="Runtime instance state, current step, and outcome status will be shown here."
        metric="0 active"
      />
      <EmptyPanel
        icon={<Boxes size={20} strokeWidth={2} aria-hidden="true" />}
        title="No saga instances."
        description="Running and completed local orchestration instances will appear here."
      />
    </PageSection>
  );
}
