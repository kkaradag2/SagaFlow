import { Inbox } from "lucide-react";

import { EmptyPanel, PageSection, SummaryPanel } from "../layout/DashboardLayout";

export function MessagesPage() {
  return (
    <PageSection>
      <SummaryPanel
        icon={<Inbox className="size-4" />}
        title="Messaging Interaction"
        description="Commands, outcome events, and correlation metadata will be inspected here."
        metric="0 messages"
      />
      <EmptyPanel
        icon={<Inbox size={20} strokeWidth={2} aria-hidden="true" />}
        title="No messages captured."
        description="Command and outcome event envelopes will appear here when runtime observability is connected."
      />
    </PageSection>
  );
}
