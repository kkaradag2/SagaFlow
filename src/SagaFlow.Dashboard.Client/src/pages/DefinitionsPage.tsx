import { GitBranch } from "lucide-react";

import { EmptyPanel, PageSection, SummaryPanel } from "../layout/DashboardLayout";

export function DefinitionsPage() {
  return (
    <PageSection>
      <SummaryPanel
        icon={<GitBranch className="size-4" />}
        title="Declarative Process Definitions"
        description="YAML-backed Saga definitions loaded by the embedded host will be listed here."
        metric="0 loaded"
      />
      <EmptyPanel
        icon={<GitBranch size={20} strokeWidth={2} aria-hidden="true" />}
        title="No definitions loaded."
        description="Loaded SagaFlow definitions will appear here once runtime metadata endpoints are connected."
      />
    </PageSection>
  );
}
