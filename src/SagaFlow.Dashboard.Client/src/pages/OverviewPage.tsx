import { Activity, GitBranch } from "lucide-react";

import { ProcessGraphPlaceholder } from "../components/ProcessGraphPlaceholder";
import { StepTimelinePlaceholder } from "../components/StepTimelinePlaceholder";
import { PageSection, SummaryPanel } from "../layout/DashboardLayout";

export function OverviewPage() {
  return (
    <PageSection>
      <SummaryPanel
        icon={<Activity className="size-4" />}
        title="Local Saga Runtime"
        description="Embedded process visibility for the host microservice."
        metric="Prototype"
      />

      <div className="grid gap-4 xl:grid-cols-[1.2fr_0.8fr]">
        <ProcessGraphPlaceholder />
        <StepTimelinePlaceholder />
      </div>
      <SummaryPanel
        icon={<GitBranch className="size-4" />}
        title="Feature Model Coverage"
        description="Definitions, coordination, messaging, state, consistency, and visibility are represented as separate dashboard concerns."
      />
    </PageSection>
  );
}
